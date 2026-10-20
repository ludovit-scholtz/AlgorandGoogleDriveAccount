using System.Security.Cryptography;
using Algorand;
using Algorand.Algod.Model;
using Arc56.Generated.ludovit_scholtz.algo_safe.AlgoSafe_e28553fc;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// Reads an AlgoSafe's state straight from its boxes and global state (the contract has no read-only ABI getters since v3.0.0) and
    /// decodes it with the generated client's struct parsers. Refuses any app whose approval program isn't a supported contract version -
    /// a different ABI would decode into nonsense.
    /// </summary>
    public sealed class AlgoSafeReader
    {
        /// <summary>How many of the newest proposals <see cref="ListProposalsAsync"/> will look at before giving up on finding matches.</summary>
        private const int MaxProposalsScanned = 200;

        private readonly IAlgoSafeChainClient _chain;

        public AlgoSafeReader(IAlgoSafeChainClient chain)
        {
            _chain = chain;
        }

        /// <summary>The approval-program hash and supported version of an application, or throws if it doesn't exist / isn't a supported AlgoSafe.</summary>
        public async Task<(Application App, string Hash, string? Version)> InspectAsync(ulong appId, CancellationToken cancellationToken = default)
        {
            var app = await _chain.GetApplicationAsync(appId, cancellationToken)
                ?? throw new AlgoSafeException("AppNotFound", $"Application {appId} does not exist on this network.");

            var program = app.Params?.ApprovalProgram?.Bytes;
            var hash = program == null ? string.Empty : Convert.ToHexStringLower(SHA256.HashData(program));
            AlgoSafeConstants.SupportedContractVersions.TryGetValue(hash, out var version);
            return (app, hash, version);
        }

        /// <summary>Like <see cref="InspectAsync"/> but also requires a supported AlgoSafe version (the builder/proposal paths).</summary>
        public async Task<Application> RequireSupportedAsync(ulong appId, CancellationToken cancellationToken = default)
        {
            var (app, hash, version) = await InspectAsync(appId, cancellationToken);
            if (version == null)
            {
                throw new AlgoSafeException(
                    "UnsupportedContract",
                    $"Application {appId} is not a supported AlgoSafe (its approval program hashes to '{hash}'). Supported versions: {string.Join(", ", AlgoSafeConstants.SupportedContractVersions.Values)}.");
            }

            return app;
        }

        public async Task<AlgoSafeView> GetSafeAsync(ulong appId, CancellationToken cancellationToken = default)
        {
            var (app, hash, version) = await InspectAsync(appId, cancellationToken);
            var safeAddress = Address.ForApplication(appId);
            var view = new AlgoSafeView
            {
                AppId = appId,
                SafeAddress = safeAddress.EncodeAsString(),
                Creator = app.Params?.Creator?.EncodeAsString() ?? string.Empty,
                ApprovalProgramHash = hash,
                ContractVersion = version,
                Supported = version != null
            };

            if (version == null)
            {
                return view;
            }

            view.SafeBalanceMicroAlgo = await _chain.GetBalanceAsync(safeAddress, cancellationToken);
            var state = ParseGlobalState(app);
            view.Name = AlgoSafeText.Clean(state.GetString("name"));
            view.Bootstrapped = state.GetUInt("boot") == 1;
            view.Paused = state.GetUInt("paused") != 0;
            view.ValidatorAppId = state.GetUInt("vapp");
            view.NextProposalId = state.GetUInt("npid");
            var nextGroupId = state.GetUInt("ngid");

            // Members can only be enumerated from the box listing; a safe too large to list still shows its groups, just without members.
            var memberNames = new List<(bool Ok, ulong GroupId, Address Account)>();
            try
            {
                memberNames = (await _chain.ListBoxNamesAsync(appId, cancellationToken))
                    .Select(n => AlgoSafeBoxNames.TryParseMember(n, out var gid, out var account) ? (Ok: true, GroupId: gid, Account: account) : default)
                    .Where(m => m.Ok)
                    .ToList();
            }
            catch (AlgoSafeException ex) when (ex.ErrorType == "TooManyBoxes")
            {
                view.MembersComplete = false;
            }

            var groups = new List<AlgoSafeGroupView>();
            for (ulong groupId = 1; groupId < nextGroupId; groupId++)
            {
                var group = await ReadGroupAsync(appId, groupId, cancellationToken);
                if (group == null)
                {
                    continue; // a dissolved custodian group leaves a gap in the id sequence
                }

                var members = new List<AlgoSafeMemberView>();
                foreach (var (_, _, account) in memberNames.Where(m => m.GroupId == groupId))
                {
                    var member = await ReadMemberAsync(appId, groupId, account, cancellationToken);
                    if (member != null)
                    {
                        members.Add(new AlgoSafeMemberView { Address = account.EncodeAsString(), Label = AlgoSafeText.Clean(member.Label), AccountType = member.AccountType });
                    }
                }

                groups.Add(ToGroupView(groupId, group, members));
            }

            view.Groups = groups;
            return view;
        }

        public async Task<(IReadOnlyList<AlgoSafeProposalSummaryView> Proposals, bool Truncated)> ListProposalsAsync(ulong appId, string? statusFilter, int limit, CancellationToken cancellationToken = default)
        {
            var app = await RequireSupportedAsync(appId, cancellationToken);
            var nextProposalId = ParseGlobalState(app).GetUInt("npid");
            var currentRound = (await _chain.GetTransactionParamsAsync(cancellationToken)).LastRound;
            var wanted = ParseStatusFilter(statusFilter);

            var results = new List<AlgoSafeProposalSummaryView>();
            var scanned = 0;
            for (var id = nextProposalId; id > 1 && results.Count < limit && scanned < MaxProposalsScanned; id--, scanned++)
            {
                var proposal = await ReadProposalAsync(appId, id - 1, cancellationToken);
                if (proposal == null || !wanted.Contains(proposal.Status))
                {
                    continue;
                }

                results.Add(ToSummary(id - 1, proposal, currentRound));
            }

            // Stopped by the scan cap (not by running out of proposals or filling the page): older proposals may match too.
            var truncated = results.Count < limit && scanned >= MaxProposalsScanned && nextProposalId - (ulong)scanned > 1;
            return (results, truncated);
        }

        public async Task<AlgoSafeProposalView> GetProposalAsync(ulong appId, ulong proposalId, Address? caller, bool includeApprovers = true, CancellationToken cancellationToken = default)
        {
            var app = await RequireSupportedAsync(appId, cancellationToken);
            var proposal = await ReadProposalAsync(appId, proposalId, cancellationToken)
                ?? throw new AlgoSafeException("ProposalNotFound", $"Proposal {proposalId} does not exist in safe {appId} (it may have been pruned after execution or cancellation).");
            var currentRound = (await _chain.GetTransactionParamsAsync(cancellationToken)).LastRound;
            var summary = ToSummary(proposalId, proposal, currentRound);

            // Who approved can only be enumerated from the box listing; a safe too large to list degrades to "unknown" rather than failing -
            // the caller's own approval is checked directly below, which is all approving or executing needs.
            var approvers = new List<string>();
            var approversComplete = true;
            if (!includeApprovers)
            {
                approversComplete = false; // not listed on purpose; the caller's own approval is still checked exactly below
            }
            else
            {
                try
                {
                    approvers = (await _chain.ListBoxNamesAsync(appId, cancellationToken))
                        .Select(n => AlgoSafeBoxNames.TryParseApproval(n, out var pid, out var account) && pid == proposalId ? account.EncodeAsString() : null)
                        .Where(a => a != null)
                        .Select(a => a!)
                        .ToList();
                }
                catch (AlgoSafeException ex) when (ex.ErrorType == "TooManyBoxes")
                {
                    approversComplete = false;
                }
            }

            var view = new AlgoSafeProposalView
            {
                Summary = summary,
                Approvers = approvers,
                ApproversComplete = approversComplete,
                CanExecute = proposal.Status == AlgoSafeConstants.StatusReady && !summary.Expired && proposal.PayloadType == AlgoSafeConstants.PayloadTypeTransactionGroup && ParseGlobalState(app).GetUInt("paused") == 0
            };

            if (proposal.PayloadType == AlgoSafeConstants.PayloadTypeTransactionGroup)
            {
                view.Transactions = await ReadProposedTransactionsAsync(appId, proposalId, proposal.NumPayloads, cancellationToken);
            }

            if (caller != null)
            {
                var callerString = caller.EncodeAsString();
                var isMember = await ReadMemberAsync(appId, proposal.GroupId, caller, cancellationToken) != null;
                var hasApproved = approvers.Contains(callerString)
                    || await _chain.GetBoxAsync(appId, AlgoSafeBoxNames.Approval(proposalId, caller), cancellationToken) != null;
                var group = await ReadGroupAsync(appId, proposal.GroupId, cancellationToken);
                var epochMatches = group != null && group.MembershipEpoch == proposal.EpochAtCreation;
                view.CallerIsMember = isMember;
                view.CallerHasApproved = hasApproved;
                view.CallerCanApprove = isMember
                    && !hasApproved
                    && epochMatches
                    && !summary.Expired
                    && (proposal.Status == AlgoSafeConstants.StatusActive || proposal.Status == AlgoSafeConstants.StatusReady);
            }

            return view;
        }

        public async Task<AlgoSafeProxy.Structs.SignerGroup?> ReadGroupAsync(ulong appId, ulong groupId, CancellationToken cancellationToken = default)
        {
            var bytes = await _chain.GetBoxAsync(appId, AlgoSafeBoxNames.Group(groupId), cancellationToken);
            return bytes == null ? null : AlgoSafeProxy.Structs.SignerGroup.Parse(bytes);
        }

        public async Task<AlgoSafeProxy.Structs.Member?> ReadMemberAsync(ulong appId, ulong groupId, Address account, CancellationToken cancellationToken = default)
        {
            var bytes = await _chain.GetBoxAsync(appId, AlgoSafeBoxNames.Member(groupId, account), cancellationToken);
            return bytes == null ? null : AlgoSafeProxy.Structs.Member.Parse(bytes);
        }

        public async Task<AlgoSafeProxy.Structs.Proposal?> ReadProposalAsync(ulong appId, ulong proposalId, CancellationToken cancellationToken = default)
        {
            var bytes = await _chain.GetBoxAsync(appId, AlgoSafeBoxNames.Proposal(proposalId), cancellationToken);
            return bytes == null ? null : AlgoSafeProxy.Structs.Proposal.Parse(bytes);
        }

        /// <summary>Looks up a deployment transaction: whether it confirmed, the application it created and what that application is.</summary>
        public async Task<AlgoSafeDeploymentView> DescribeDeploymentAsync(string transactionId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(transactionId))
            {
                throw new AlgoSafeException("InvalidRequest", "A transaction id is required.");
            }

            var pending = await _chain.GetPendingTransactionAsync(transactionId.Trim(), cancellationToken)
                ?? throw new AlgoSafeException("TransactionNotFound", $"Transaction {transactionId} is not known to the node: not broadcast yet, on a different network, or too old for the node to still remember (nodes only keep recent transactions - do NOT redeploy; look the application up by its id instead if you have it).");

            var confirmedRound = (pending as Algorand.Algod.Model.Transactions.Transaction)?.ConfirmedRound ?? 0;
            if (confirmedRound == 0)
            {
                return new AlgoSafeDeploymentView { Confirmed = false, Kind = "Pending" };
            }

            var appId = (pending as Algorand.Algod.Model.Transactions.ApplicationCreateTransaction)?.ApplicationIndex ?? 0;
            if (appId == 0)
            {
                throw new AlgoSafeException("NotADeployment", $"Transaction {transactionId} confirmed but did not create an application.");
            }

            var (_, hash, version) = await InspectAsync(appId, cancellationToken);
            var kind = version != null
                ? "AlgoSafe"
                : string.Equals(hash, AlgoSafeConstants.ValidatorApprovalSha256Hex, StringComparison.OrdinalIgnoreCase) ? "AlgoSafeTxnValidator" : "Other";
            return new AlgoSafeDeploymentView
            {
                Confirmed = true,
                ApplicationId = appId,
                Kind = kind,
                SafeAddress = kind == "AlgoSafe" ? Address.ForApplication(appId).EncodeAsString() : null,
                ConfirmedRound = confirmedRound
            };
        }

        public async Task<AlgoSafeSafeState> ReadSafeStateAsync(ulong appId, CancellationToken cancellationToken = default)
        {
            var app = await RequireSupportedAsync(appId, cancellationToken);
            var state = ParseGlobalState(app);
            return new AlgoSafeSafeState(
                state.GetUInt("boot") == 1,
                state.GetUInt("paused") != 0,
                state.GetUInt("vapp"),
                state.GetUInt("npid"),
                state.GetUInt("ngid"),
                app.Params?.Creator ?? new Address(new byte[32]));
        }

        private async Task<IReadOnlyList<AlgoSafeProposedTransactionView>> ReadProposedTransactionsAsync(ulong appId, ulong proposalId, ulong numPayloads, CancellationToken cancellationToken)
        {
            var transactions = new List<AlgoSafeProposedTransactionView>();
            for (ulong chunk = 1; chunk <= numPayloads; chunk++)
            {
                var bytes = await _chain.GetBoxAsync(appId, AlgoSafeBoxNames.TransactionGroup(proposalId, chunk), cancellationToken);
                if (bytes == null)
                {
                    continue; // a chunk slot may be skipped
                }

                foreach (var entry in AlgoSafePayloadCodec.DecodeEnvelopeArray(bytes))
                {
                    transactions.Add(DescribeEntry(entry));
                }
            }

            return transactions;
        }

        /// <summary>What a proposed transaction's sender reads as when it is the safe's own account (the zero address on the wire).</summary>
        internal const string SafeItself = "(the safe itself)";

        internal static AlgoSafeProposedTransactionView DescribeEntry(AlgoSafePayloadEntry entry)
        {
            var zero = AlgoSafePayloadCodec.ZeroAddress;
            string SenderText(Address sender) => sender.Equals(zero) ? SafeItself : sender.EncodeAsString();

            switch (entry.TxType)
            {
                case AlgoSafeConstants.TxPayment:
                {
                    var payment = AlgoSafePayloadCodec.DecodePayment(entry.Data);
                    return new AlgoSafeProposedTransactionView
                    {
                        Kind = "Payment",
                        Sender = SenderText(payment.Sender),
                        Receiver = payment.Receiver.EncodeAsString(),
                        Amount = payment.Amount,
                        AssetId = 0,
                        ClosesAccount = payment.HasClose != 0,
                        CloseTo = payment.HasClose != 0 ? payment.CloseRemainderTo.EncodeAsString() : null,
                        Note = AlgoSafeText.Clean(payment.Note)
                    };
                }

                case AlgoSafeConstants.TxAsset:
                {
                    var asset = AlgoSafePayloadCodec.DecodeAsset(entry.Data);
                    return new AlgoSafeProposedTransactionView
                    {
                        Kind = "AssetTransfer",
                        Sender = SenderText(asset.Sender),
                        Receiver = asset.AssetReceiver.EncodeAsString(),
                        Amount = asset.AssetAmount,
                        AssetId = asset.XferAsset,
                        ClosesAccount = asset.HasAssetClose != 0,
                        CloseTo = asset.HasAssetClose != 0 ? asset.AssetCloseTo.EncodeAsString() : null,
                        Note = AlgoSafeText.Clean(asset.Note)
                    };
                }

                default:
                    // App calls, key registration, asset config and rekey are valid proposal entries this server doesn't decode.
                    return new AlgoSafeProposedTransactionView { Kind = $"Unsupported({entry.TxType})", Sender = SafeItself };
            }
        }

        private static AlgoSafeGroupView ToGroupView(ulong groupId, AlgoSafeProxy.Structs.SignerGroup group, IReadOnlyList<AlgoSafeMemberView> members) => new()
        {
            GroupId = groupId,
            Name = AlgoSafeText.Clean(group.Name),
            Active = group.Active != 0,
            GroupType = group.GroupType == AlgoSafeConstants.GroupTypeCustodian ? "Custodian" : "Standard",
            Threshold = group.Threshold,
            MemberCount = group.MemberCount,
            AllowedActionsMask = group.AllowedActions,
            AllowedActions = AlgoSafeConstants.ActionNames(group.AllowedActions),
            AdminPrivilegesMask = group.AdminPrivileges,
            AdminPrivileges = AlgoSafeConstants.PrivilegeNames(group.AdminPrivileges),
            LimitAssetId = group.LimitAssetId,
            DailyLimit = group.DailyLimit,
            DailyUsage = group.DailyUsage,
            MonthlyLimit = group.MonthlyLimit,
            MonthlyUsage = group.MonthlyUsage,
            CooldownRounds = group.CooldownRounds,
            LastExecutionRound = group.LastExecutionRound,
            Members = members
        };

        private static AlgoSafeProposalSummaryView ToSummary(ulong proposalId, AlgoSafeProxy.Structs.Proposal proposal, ulong currentRound) => new()
        {
            ProposalId = proposalId,
            GroupId = proposal.GroupId,
            Status = AlgoSafeConstants.StatusName(proposal.Status),
            Type = proposal.PayloadType switch
            {
                AlgoSafeConstants.PayloadTypeTransactionGroup => "TransactionGroup",
                AlgoSafeConstants.PayloadTypeAdmin => "AdminChange",
                var other => $"Other({other})"
            },
            ApprovalsCount = proposal.ApprovalsCount,
            Threshold = proposal.Threshold,
            ExpiryRound = proposal.ExpiryRound,
            Expired = currentRound > proposal.ExpiryRound,
            Proposer = proposal.Proposer.EncodeAsString(),
            PayloadVersion = proposal.PayloadVersion,
            TotalTransactions = proposal.TotalTxns
        };

        /// <summary>Maps a tool's status filter to the status values it selects. Defaults to the open ones (Active + Ready).</summary>
        internal static HashSet<ulong> ParseStatusFilter(string? filter) => (filter ?? "open").Trim().ToLowerInvariant() switch
        {
            "all" => new HashSet<ulong> { AlgoSafeConstants.StatusActive, AlgoSafeConstants.StatusReady, AlgoSafeConstants.StatusExecuted, AlgoSafeConstants.StatusCancelled },
            "open" or "pending" or "" => new HashSet<ulong> { AlgoSafeConstants.StatusActive, AlgoSafeConstants.StatusReady },
            "active" => new HashSet<ulong> { AlgoSafeConstants.StatusActive },
            "ready" => new HashSet<ulong> { AlgoSafeConstants.StatusReady },
            "executed" => new HashSet<ulong> { AlgoSafeConstants.StatusExecuted },
            "cancelled" or "canceled" => new HashSet<ulong> { AlgoSafeConstants.StatusCancelled },
            _ => throw new AlgoSafeException("InvalidRequest", $"Unknown status filter '{filter}'. Use one of: open (default), all, active, ready, executed, cancelled.")
        };

        internal static GlobalStateReader ParseGlobalState(Application app) => new(app.Params?.GlobalState);

        /// <summary>Reads the safe's global state, keyed by the key's text.</summary>
        internal sealed class GlobalStateReader
        {
            private readonly Dictionary<string, TealValue> _values = new(StringComparer.Ordinal);

            public GlobalStateReader(IEnumerable<TealKeyValue>? state)
            {
                foreach (var item in state ?? Array.Empty<TealKeyValue>())
                {
                    _values[System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(item.Key))] = item.Value;
                }
            }

            public ulong GetUInt(string key) => _values.TryGetValue(key, out var value) ? value.Uint : 0;

            public string GetString(string key) =>
                _values.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value.Bytes)
                    ? System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(value.Bytes))
                    : string.Empty;
        }
    }

    /// <summary>The safe-wide flags and counters the builders need.</summary>
    public sealed record AlgoSafeSafeState(bool Bootstrapped, bool Paused, ulong ValidatorAppId, ulong NextProposalId, ulong NextGroupId, Address Creator);
}
