using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Arc56.Generated.ludovit_scholtz.algo_safe.AlgoSafeTxnValidator_8a48eb95;
using Arc56.Generated.ludovit_scholtz.algo_safe.AlgoSafe_e28553fc;
using AVM.ClientGenerator.Core;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>One value transfer a proposal will make from the safe's own account: ALGO when <see cref="AssetId"/> is 0, otherwise an ASA.</summary>
    public sealed record AlgoSafeTransferRequest(string Receiver, ulong Amount, ulong AssetId = 0, string? Note = null);

    /// <summary>The first signer group to create when bootstrapping a safe.</summary>
    public sealed record AlgoSafeBootstrapPlan(
        string GroupName,
        IReadOnlyList<(string Address, string Label)>? Members = null,
        ulong Threshold = 1,
        ulong AllowedActions = AlgoSafeConstants.ActAll,
        ulong LimitAssetId = 0,
        ulong DailyLimit = 0,
        ulong MonthlyLimit = 0,
        ulong CooldownRounds = 0);

    /// <summary>
    /// Builds the unsigned AlgoSafe transactions: pre-checks the on-chain state (so an agent gets "you are not a member of group 1" rather than
    /// an opaque AVM assertion), builds the calls with the generated ARC-56 client (address-only sender - no key material), then fills in the
    /// references and fees from simulation (<see cref="AlgoSafeResourcePopulator"/>) so what is returned is known to run.
    /// </summary>
    public sealed class AlgoSafeTransactionBuilder
    {
        /// <summary>Simulation budgets tried in turn for an execution: more opcode budget is requested (one pooled inner call per ~700) only if the cheaper one runs out.</summary>
        private static readonly ulong[] ExecutionBudgets = [AlgoSafeConstants.ExecutionEnsureBudget, 20_000, 60_000];

        /// <summary>Box references per call are scarce (max 8 in total), and every member is one box.</summary>
        internal const int MaxBootstrapMembers = 6;

        private readonly IAlgoSafeChainClient _chain;
        private readonly AlgoSafeReader _reader;

        public AlgoSafeTransactionBuilder(IAlgoSafeChainClient chain, AlgoSafeReader reader)
        {
            _chain = chain;
            _reader = reader;
        }

        // ───────────────────────── deployment ─────────────────────────

        /// <summary>Step 0 (only if the network has no validator yet): deploy the stateless, immutable AlgoSafeTxnValidator library.</summary>
        public async Task<AlgoSafeBuiltStep> BuildDeployValidatorAsync(Address sender, CancellationToken cancellationToken = default)
        {
            var parameters = await _chain.GetTransactionParamsAsync(cancellationToken);
            var proxy = new AlgoSafeTxnValidatorProxy(_chain.Algod, 0) { TransParamsOverride = parameters };
            var transactions = await proxy.CreateApplication_Transactions(AsAccount(sender), null, string.Empty, 1000, _tx_callType: OnCompleteType.CreateApplication);
            return await FinishAsync(transactions, parameters, "Deploy the AlgoSafeTxnValidator library (one per network is enough; it is immutable and holds no funds).", cancellationToken);
        }

        /// <summary>Step 1: deploy the safe application itself, pinned to a validator whose bytecode hash is verified here and again by the contract.</summary>
        public async Task<AlgoSafeBuiltStep> BuildCreateSafeAsync(Address sender, string name, ulong validatorAppId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new AlgoSafeException("InvalidRequest", "A safe name is required.");
            }

            await VerifyValidatorAsync(validatorAppId, cancellationToken);
            var parameters = await _chain.GetTransactionParamsAsync(cancellationToken);
            var proxy = new AlgoSafeProxy(_chain.Algod, 0) { TransParamsOverride = parameters };

            // The contract reads the validator's approval program in createApplication, so the validator must be a foreign app of the call.
            var transactions = await proxy.CreateApplication_Transactions(
                name.Trim(), validatorAppId, AsAccount(sender), null, string.Empty, 1000,
                _tx_apps: new List<ulong> { validatorAppId },
                _tx_callType: OnCompleteType.CreateApplication);
            return await FinishAsync(transactions, parameters, $"Deploy the AlgoSafe '{name.Trim()}' application (contract {AlgoSafeConstants.SupportedContractVersions[AlgoSafeConstants.LatestApprovalSha256Hex]}) pinned to validator app {validatorAppId}.", cancellationToken);
        }

        /// <summary>
        /// Step 2: fund the new safe's account and bootstrap its first signer group - one atomic group. The simple plan (no members) is the contract's
        /// <c>bootstrap</c>: a 1-of-1 "all rights" group with the creator as the only member; otherwise <c>bootstrapGroup</c> + <c>finalizeBootstrap</c>.
        /// </summary>
        public async Task<AlgoSafeBuiltStep> BuildBootstrapAsync(Address sender, ulong appId, ulong depositMicroAlgo, AlgoSafeBootstrapPlan plan, CancellationToken cancellationToken = default)
        {
            await _reader.RequireSupportedAsync(appId, cancellationToken);
            var state = await _reader.ReadSafeStateAsync(appId, cancellationToken);
            if (state.Bootstrapped)
            {
                throw new AlgoSafeException("AlreadyBootstrapped", $"Safe {appId} is already bootstrapped.");
            }

            if (!state.Creator.Equals(sender))
            {
                throw new AlgoSafeException("NotCreator", $"Only the safe's creator ({state.Creator.EncodeAsString()}) can bootstrap it; the sender is {sender.EncodeAsString()}.");
            }

            if (depositMicroAlgo == 0)
            {
                throw new AlgoSafeException("InvalidRequest", "A deposit is required: the safe's account must hold its minimum balance and the storage of its group/member/proposal boxes (about 2 ALGO is a sensible start).");
            }

            if (string.IsNullOrWhiteSpace(plan.GroupName))
            {
                throw new AlgoSafeException("InvalidRequest", "A group name is required.");
            }

            var parameters = await _chain.GetTransactionParamsAsync(cancellationToken);
            var proxy = new AlgoSafeCallProxy(_chain.Algod, appId) { TransParamsOverride = parameters };
            var deposit = PaymentTransaction.GetPaymentTransactionFromNetworkTransactionParameters(sender, proxy.AppAddress, depositMicroAlgo, "AlgoSafe deposit", parameters);
            var preTransactions = new List<Transaction> { deposit };

            List<Transaction> transactions;
            string summary;
            var hasLimits = plan.DailyLimit != 0 || plan.MonthlyLimit != 0 || plan.CooldownRounds != 0;
            if ((plan.Members == null || plan.Members.Count == 0) && !hasLimits)
            {
                transactions = await proxy.Bootstrap_Transactions(plan.GroupName.Trim(), AsAccount(sender), null, string.Empty, 1000, _tx_transactions: preTransactions);
                summary = $"Fund the safe with {FormatAlgo(depositMicroAlgo)} ALGO and bootstrap group '{plan.GroupName.Trim()}' (1-of-1, all rights, you as the only member).";
            }
            else
            {
                // Spending limits only exist on a group created through bootstrapGroup; the creator is then its only member.
                if (plan.Members == null || plan.Members.Count == 0)
                {
                    plan = plan with { Members = new List<(string, string)> { (sender.EncodeAsString(), "owner") }, Threshold = 1 };
                }

                var (encodedSeed, members) = ValidatePlan(plan);
                transactions = await proxy.BuildBootstrapGroupAsync(encodedSeed, members, 0, AsAccount(sender), preTransactions);
                transactions = await proxy.FinalizeBootstrap_Transactions(AsAccount(sender), null, string.Empty, 1000, _tx_transactions: transactions);
                summary = $"Fund the safe with {FormatAlgo(depositMicroAlgo)} ALGO, create group '{plan.GroupName.Trim()}' ({plan.Threshold}-of-{plan.Members!.Count}) and finalize the bootstrap.";
            }

            return await FinishAsync(transactions, parameters, summary, cancellationToken);
        }

        // ───────────────────────── proposals ─────────────────────────

        public async Task<AlgoSafeBuiltStep> BuildProposeAsync(
            Address sender,
            ulong appId,
            ulong groupId,
            IReadOnlyList<AlgoSafeTransferRequest> transfers,
            ulong expiryRounds,
            bool executeNow,
            CancellationToken cancellationToken = default)
        {
            if (transfers == null || transfers.Count == 0)
            {
                throw new AlgoSafeException("InvalidRequest", "At least one transfer is required.");
            }

            if (transfers.Count > AlgoSafeConstants.MaxGroupTransactions)
            {
                throw new AlgoSafeException("InvalidRequest", $"A proposal can hold at most {AlgoSafeConstants.MaxGroupTransactions} transactions (an atomic group limit).");
            }

            if (expiryRounds < AlgoSafeConstants.MinProposalLifetimeRounds || expiryRounds > AlgoSafeConstants.MaxProposalLifetimeRounds)
            {
                throw new AlgoSafeException("InvalidRequest", $"expiryRounds must be between {AlgoSafeConstants.MinProposalLifetimeRounds} and {AlgoSafeConstants.MaxProposalLifetimeRounds} (about {AlgoSafeConstants.MaxProposalLifetimeRounds * 28 / 10 / 86400} days at ~2.8s/round).");
            }

            await _reader.RequireSupportedAsync(appId, cancellationToken);
            var state = await _reader.ReadSafeStateAsync(appId, cancellationToken);
            if (!state.Bootstrapped)
            {
                throw new AlgoSafeException("NotBootstrapped", $"Safe {appId} has not been bootstrapped yet - call bootstrapAlgoSafe first.");
            }

            if (state.Paused)
            {
                throw new AlgoSafeException("SafePaused", $"Safe {appId} is paused; transaction proposals are blocked until it is unpaused.");
            }

            var group = await _reader.ReadGroupAsync(appId, groupId, cancellationToken)
                ?? throw new AlgoSafeException("GroupNotFound", $"Safe {appId} has no signer group {groupId}.");
            if (group.Active == 0)
            {
                throw new AlgoSafeException("GroupDisabled", $"Signer group {groupId} is disabled.");
            }

            if (await _reader.ReadMemberAsync(appId, groupId, sender, cancellationToken) == null)
            {
                throw new AlgoSafeException("NotAMember", $"{sender.EncodeAsString()} is not a member of signer group {groupId}, so it cannot propose for it.");
            }

            var payload = new List<AlgoSafePayloadEntry>();
            foreach (var transfer in transfers)
            {
                payload.Add(EncodeTransfer(transfer, group.AllowedActions));
            }

            var encodedSize = AlgoSafePayloadCodec.EncodeEnvelopeArray(payload).Length;
            if (encodedSize > AlgoSafeConstants.MaxPayloadArgumentBytes)
            {
                throw new AlgoSafeException(
                    "ProposalTooLarge",
                    $"These {transfers.Count} transfer(s) encode to {encodedSize} bytes, more than the {AlgoSafeConstants.MaxPayloadArgumentBytes} bytes one call can carry (about 14 payments or 13 asset transfers without notes). Split them into several proposals.");
            }

            if (executeNow && group.Threshold != 1)
            {
                throw new AlgoSafeException(
                    "ThresholdNotOne",
                    $"executeNow needs a 1-of-N group, but group {groupId} requires {group.Threshold} approvals. Propose without executeNow, collect approvals (approveAlgoSafeProposal), then executeAlgoSafeProposal.");
            }

            var parameters = await _chain.GetTransactionParamsAsync(cancellationToken);
            var expiryRound = parameters.LastRound + expiryRounds;
            var summary = $"Propose {transfers.Count} transfer(s) for group {groupId} of safe {appId}{(executeNow ? " and execute immediately" : string.Empty)} (expires at round {expiryRound}).";

            if (!executeNow)
            {
                var transactions = await NewProxy(appId, parameters).BuildProposeTransactionGroupAsync(groupId, payload, expiryRound, false, 0, AsAccount(sender));
                return await FinishAsync(transactions, parameters, summary, cancellationToken);
            }

            return await WithBudgetsAsync(
                budget => NewProxy(appId, parameters).BuildProposeTransactionGroupAsync(groupId, payload, expiryRound, true, budget, AsAccount(sender)),
                parameters,
                summary,
                cancellationToken);
        }

        public async Task<AlgoSafeBuiltStep> BuildApproveAsync(Address sender, ulong appId, ulong proposalId, CancellationToken cancellationToken = default)
        {
            var view = await _reader.GetProposalAsync(appId, proposalId, sender, cancellationToken);
            var summary = view.Summary;
            if (summary.Type != "TransactionGroup")
            {
                throw new AlgoSafeException("UnsupportedProposal", $"Proposal {proposalId} is a governance (admin) change, whose content this server cannot show; approve it in the AlgoSafe app instead.");
            }

            var undecoded = view.Transactions.Where(t => t.Kind.StartsWith("Unsupported", StringComparison.Ordinal)).ToList();
            if (undecoded.Count > 0)
            {
                throw new AlgoSafeException("UnsupportedProposal", $"Proposal {proposalId} contains {undecoded.Count} transaction(s) this server cannot decode (e.g. app calls, key registration, asset configuration or rekeys); approving blind is refused - review and approve it in the AlgoSafe app instead.");
            }

            if (view.CallerIsMember != true)
            {
                throw new AlgoSafeException("NotAMember", $"{sender.EncodeAsString()} is not a member of signer group {summary.GroupId}, so it cannot approve proposal {proposalId}.");
            }

            if (view.CallerHasApproved == true)
            {
                throw new AlgoSafeException("AlreadyApproved", $"{sender.EncodeAsString()} has already approved proposal {proposalId}.");
            }

            if (summary.Status is not ("Active" or "Ready"))
            {
                throw new AlgoSafeException("NotApprovable", $"Proposal {proposalId} is {summary.Status} and can no longer be approved.");
            }

            if (summary.Expired)
            {
                throw new AlgoSafeException("ProposalExpired", $"Proposal {proposalId} expired at round {summary.ExpiryRound}.");
            }

            if (view.CallerCanApprove != true)
            {
                throw new AlgoSafeException("NotApprovable", $"Proposal {proposalId} can't be approved: the group's membership changed since it was created (cancel it and propose again).");
            }

            var parameters = await _chain.GetTransactionParamsAsync(cancellationToken);

            // Bound to the payload version just read: if the proposer changes the payload before this is mined, the contract refuses
            // ("payload changed since review") instead of counting an approval for content the signer never saw.
            var transactions = await NewProxy(appId, parameters).ApproveProposal_Transactions(
                proposalId, summary.PayloadVersion, 0, AsAccount(sender), null, string.Empty, 1000);
            return await FinishAsync(transactions, parameters, $"Approve proposal {proposalId} of safe {appId} (payload version {summary.PayloadVersion}; {summary.ApprovalsCount + 1}/{summary.Threshold} approvals after this).", cancellationToken);
        }

        public async Task<AlgoSafeBuiltStep> BuildExecuteAsync(Address sender, ulong appId, ulong proposalId, CancellationToken cancellationToken = default)
        {
            var view = await _reader.GetProposalAsync(appId, proposalId, sender, cancellationToken);
            var summary = view.Summary;
            if (summary.Type != "TransactionGroup")
            {
                throw new AlgoSafeException("UnsupportedProposal", $"Proposal {proposalId} is a governance (admin) change; this server only executes transaction-group proposals.");
            }

            if (summary.Status != "Ready")
            {
                throw new AlgoSafeException(
                    "NotReady",
                    summary.Status == "Active"
                        ? $"Proposal {proposalId} has {summary.ApprovalsCount}/{summary.Threshold} approvals - it becomes executable once the threshold is met."
                        : $"Proposal {proposalId} is {summary.Status} and cannot be executed.");
            }

            if (summary.Expired)
            {
                throw new AlgoSafeException("ProposalExpired", $"Proposal {proposalId} expired at round {summary.ExpiryRound}.");
            }

            var state = await _reader.ReadSafeStateAsync(appId, cancellationToken);
            if (state.Paused)
            {
                throw new AlgoSafeException("SafePaused", $"Safe {appId} is paused; transaction proposals cannot be executed until it is unpaused.");
            }

            var parameters = await _chain.GetTransactionParamsAsync(cancellationToken);
            return await WithBudgetsAsync(
                budget => NewProxy(appId, parameters).ExecuteProposal_Transactions(proposalId, budget, AsAccount(sender), null, string.Empty, 1000),
                parameters,
                $"Execute proposal {proposalId} of safe {appId}: the safe sends {view.Transactions.Count} transaction(s) from its own account.",
                cancellationToken);
        }

        // ───────────────────────── helpers ─────────────────────────

        private AlgoSafeCallProxy NewProxy(ulong appId, TransactionParametersResponse parameters) =>
            new(_chain.Algod, appId) { TransParamsOverride = parameters };

        private static Account AsAccount(Address address) => new() { Address = address };

        private async Task VerifyValidatorAsync(ulong validatorAppId, CancellationToken cancellationToken)
        {
            var app = await _chain.GetApplicationAsync(validatorAppId, cancellationToken)
                ?? throw new AlgoSafeException("ValidatorNotFound", $"Validator application {validatorAppId} does not exist on this network.");
            var program = app.Params?.ApprovalProgram?.Bytes;
            var hash = program == null ? string.Empty : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(program));
            if (!string.Equals(hash, AlgoSafeConstants.ValidatorApprovalSha256Hex, StringComparison.OrdinalIgnoreCase))
            {
                throw new AlgoSafeException(
                    "ValidatorMismatch",
                    $"Application {validatorAppId} is not the AlgoSafeTxnValidator (its approval program hashes to '{hash}', expected '{AlgoSafeConstants.ValidatorApprovalSha256Hex}'); a safe can only be pinned to the genuine validator.");
            }
        }

        private static AlgoSafePayloadEntry EncodeTransfer(AlgoSafeTransferRequest transfer, ulong groupAllowedActions)
        {
            var receiver = ParseAddress(transfer.Receiver, "receiver");
            var zero = AlgoSafePayloadCodec.ZeroAddress;
            if (transfer.AssetId == 0)
            {
                if (transfer.Amount == 0)
                {
                    throw new AlgoSafeException("InvalidRequest", "A payment amount must be greater than 0.");
                }

                RequireAction(groupAllowedActions, AlgoSafeConstants.ActPay, "payments");
                return new AlgoSafePayloadEntry(AlgoSafeConstants.TxPayment, AlgoSafePayloadCodec.EncodePayment(zero, receiver, transfer.Amount, 0, zero, transfer.Note ?? string.Empty));
            }

            RequireAction(groupAllowedActions, AlgoSafeConstants.ActAxfer, "asset transfers");
            return new AlgoSafePayloadEntry(AlgoSafeConstants.TxAsset, AlgoSafePayloadCodec.EncodeAsset(zero, transfer.AssetId, receiver, transfer.Amount, 0, zero, transfer.Note ?? string.Empty));
        }

        private static void RequireAction(ulong allowedActions, ulong action, string what)
        {
            if ((allowedActions & action) == 0)
            {
                throw new AlgoSafeException("ActionNotAllowed", $"This signer group is not allowed to propose {what} (its allowed actions: {string.Join(", ", AlgoSafeConstants.ActionNames(allowedActions))}).");
            }
        }

        private static (byte[] EncodedSeed, IReadOnlyList<(Address Address, ulong AccountType, string Label)> Members) ValidatePlan(AlgoSafeBootstrapPlan plan)
        {
            var members = plan.Members!;
            if (members.Count > MaxBootstrapMembers)
            {
                throw new AlgoSafeException("InvalidRequest", $"Bootstrapping supports at most {MaxBootstrapMembers} initial members (each member is a storage box a single call can reference); add the rest later through governance proposals.");
            }

            if (plan.Threshold < 1 || plan.Threshold > (ulong)members.Count)
            {
                throw new AlgoSafeException("InvalidRequest", $"threshold must be between 1 and the number of members ({members.Count}).");
            }

            if (plan.AllowedActions > AlgoSafeConstants.ActAll)
            {
                throw new AlgoSafeException("InvalidRequest", $"allowedActions must be a bitmask of at most {AlgoSafeConstants.ActAll}.");
            }

            var addresses = new List<Address>();
            var parsed = new List<(Address Address, ulong AccountType, string Label)>();
            foreach (var (addressText, label) in members)
            {
                var address = ParseAddress(addressText, "member");
                if (address.Equals(AlgoSafePayloadCodec.ZeroAddress))
                {
                    throw new AlgoSafeException("InvalidRequest", "A member address cannot be the zero address.");
                }

                if (addresses.Contains(address))
                {
                    throw new AlgoSafeException("InvalidRequest", $"Duplicate member {addressText}.");
                }

                addresses.Add(address);
                parsed.Add((address, 1, label ?? string.Empty));
            }

            // The bootstrap group must be able to govern: finalizeBootstrap requires an active group with the group-management privilege.
            var seed = AlgoSafePayloadCodec.EncodeGroupSeed(
                plan.GroupName.Trim(), plan.Threshold, AlgoSafeConstants.PrivAll, plan.AllowedActions, plan.LimitAssetId, plan.DailyLimit, plan.MonthlyLimit, plan.CooldownRounds, AlgoSafeConstants.GroupTypeStandard);
            return (seed, parsed);
        }

        private static Address ParseAddress(string text, string what)
        {
            try
            {
                return new Address(text);
            }
            catch (Exception)
            {
                throw new AlgoSafeException("InvalidRequest", $"'{text}' is not a valid Algorand {what} address.");
            }
        }

        /// <summary>Builds with the cheapest opcode budget first and retries with more only when the simulation says the budget ran out.</summary>
        private async Task<AlgoSafeBuiltStep> WithBudgetsAsync(
            Func<ulong, Task<List<Transaction>>> build,
            TransactionParametersResponse parameters,
            string summary,
            CancellationToken cancellationToken)
        {
            AlgoSafeException? last = null;
            foreach (var budget in ExecutionBudgets)
            {
                var transactions = await build(budget);
                try
                {
                    return await FinishAsync(transactions, parameters, summary, cancellationToken);
                }
                catch (AlgoSafeException ex) when (ex.ErrorType == "SimulationFailed" && ex.Message.Contains("budget", StringComparison.OrdinalIgnoreCase))
                {
                    last = ex;
                }
            }

            throw last!;
        }

        private static async Task<AlgoSafeBuiltStep> FinishAsync(List<Transaction> transactions, TransactionParametersResponse parameters, string summary, IAlgoSafeChainClient chain, CancellationToken cancellationToken)
        {
            var populated = await AlgoSafeResourcePopulator.PopulateAsync(chain, transactions, parameters.MinFee, cancellationToken: cancellationToken);
            return new AlgoSafeBuiltStep
            {
                UnsignedTransactions = populated.Transactions.Select(t => Algorand.Utils.Encoder.EncodeToMsgPackOrdered(t)).ToList(),
                TotalFeeMicroAlgo = populated.TotalFeeMicroAlgo,
                InnerTransactionCount = populated.InnerTransactionCount,
                Summary = summary
            };
        }

        private Task<AlgoSafeBuiltStep> FinishAsync(List<Transaction> transactions, TransactionParametersResponse parameters, string summary, CancellationToken cancellationToken) =>
            FinishAsync(transactions, parameters, summary, _chain, cancellationToken);

        private static string FormatAlgo(ulong microAlgo) => (microAlgo / 1_000_000m).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }
}
