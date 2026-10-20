using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>The finished, group-ready transactions plus what was needed to make them run.</summary>
    public sealed record AlgoSafePopulationResult(IReadOnlyList<Transaction> Transactions, ulong TotalFeeMicroAlgo, int InnerTransactionCount);

    /// <summary>
    /// The generated contract proxies only build the bare application calls - they add no box/account/asset/app references and cover no
    /// inner-transaction fees. This fills those in from algod's own simulation (<c>AllowUnnamedResources</c>: the node reports exactly which
    /// resources a call touched without references) and sets each app call's fee to cover itself plus every inner transaction it spawned
    /// (fee pooling), re-simulating until nothing changes - so the last simulation is also the proof that the returned group runs.
    /// </summary>
    public static class AlgoSafeResourcePopulator
    {
        private const int MaxResourceAttempts = 6;

        /// <summary>A call with inner transactions fails simulation outright when its fee is too small, so the fee is doubled (up to this multiple of the min fee) until it runs, then trimmed to the exact need.</summary>
        private const ulong MaxFeeMultiple = 256;

        /// <summary>Per-transaction reference limits (consensus parameters): 4 accounts, and 8 references in total across accounts/apps/assets/boxes.</summary>
        private const int MaxAccounts = 4;
        private const int MaxTotalReferences = 8;

        public static async Task<AlgoSafePopulationResult> PopulateAsync(
            IAlgoSafeChainClient chain,
            IReadOnlyList<Transaction> group,
            ulong minFee,
            CancellationToken cancellationToken = default)
        {
            var innerCount = 0;
            var resourceAttempts = 0;
            var feeMultiple = 1UL;
            var attempts = 0;

            while (attempts++ < MaxResourceAttempts + 12)
            {
                AssignGroupId(group);
                var result = await chain.SimulateAsync(group, cancellationToken);

                if (!string.IsNullOrEmpty(result.FailureMessage))
                {
                    // A call that spawns inner transactions can't even be traced until its fee covers them: escalate and retry.
                    if (IsFeeTooSmall(result.FailureMessage) && feeMultiple < MaxFeeMultiple)
                    {
                        feeMultiple *= 2;
                        foreach (var appCall in CallsWithRoomForInner(group))
                        {
                            appCall.Fee = Math.Max(appCall.Fee, minFee * feeMultiple);
                        }

                        continue;
                    }

                    throw new AlgoSafeException("SimulationFailed", $"The network rejected this transaction when simulated: {Explain(result.FailureMessage)}")
                    {
                        BudgetExhausted = result.FailureMessage.Contains("budget", StringComparison.OrdinalIgnoreCase)
                    };
                }

                var changed = false;
                var results = result.TxnResults?.ToList() ?? new List<SimulateTransactionResult>();
                innerCount = 0;
                for (var i = 0; i < group.Count && i < results.Count; i++)
                {
                    if (group[i] is not ApplicationCallTransaction appCall)
                    {
                        continue;
                    }

                    changed |= ApplyUnnamedResources(appCall, results[i].UnnamedResourcesAccessed);

                    // Exact fee: the call itself plus one min fee per inner transaction it spawned - never more, never less.
                    var inner = CountInner(results[i].ExecTrace);
                    innerCount += inner;
                    if (appCall is not ApplicationCreateTransaction)
                    {
                        var exactFee = minFee * (ulong)(1 + inner);
                        if (appCall.Fee != exactFee)
                        {
                            appCall.Fee = exactFee;
                            changed = true;
                        }
                    }
                }

                // Resources reported only at group level (not attributed to one transaction) go on the call that references them.
                if (HasAny(result.UnnamedResourcesAccessed))
                {
                    changed |= ApplyGroupLevelResources(group, result.UnnamedResourcesAccessed!);
                }

                foreach (var appCall in group.OfType<ApplicationCallTransaction>())
                {
                    AssertWithinReferenceLimits(appCall);
                }

                if (!changed)
                {
                    AssignGroupId(group);
                    var totalFee = group.Aggregate(0UL, (sum, t) => sum + t.Fee);
                    return new AlgoSafePopulationResult(group, totalFee, innerCount);
                }

                if (++resourceAttempts > MaxResourceAttempts)
                {
                    break;
                }
            }

            throw new AlgoSafeException("SimulationDidNotConverge", "Could not determine the references and fees this call needs - the node kept reporting new requirements.");
        }

        /// <summary>Clears and recomputes the atomic group id (it commits to every other field, so it must be the last thing set).</summary>
        private static void AssignGroupId(IReadOnlyList<Transaction> group)
        {
            foreach (var transaction in group)
            {
                transaction.Group = null;
            }

            if (group.Count > 1)
            {
                var id = TxGroup.ComputeGroupID(group.ToArray());
                foreach (var transaction in group)
                {
                    transaction.Group = id;
                }
            }
        }

        /// <summary>The app calls that can spawn inner transactions: every call except an application creation (which runs no inner transactions here).</summary>
        private static IEnumerable<ApplicationCallTransaction> CallsWithRoomForInner(IReadOnlyList<Transaction> group) =>
            group.OfType<ApplicationCallTransaction>().Where(c => c is not ApplicationCreateTransaction);

        private static bool IsFeeTooSmall(string message) =>
            message.Contains("fee", StringComparison.OrdinalIgnoreCase) && message.Contains("too small", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Makes algod's failure text usable: drops the huge struct dump it embeds and keeps the "Details: app=..., pc=..." tail.
        /// </summary>
        internal static string Explain(string failureMessage)
        {
            var cleaned = failureMessage;
            var dump = cleaned.IndexOf(" []transactions.", StringComparison.Ordinal);
            if (dump > 0)
            {
                var details = cleaned.LastIndexOf("Details:", StringComparison.Ordinal);
                cleaned = cleaned[..dump] + (details > dump ? " " + cleaned[details..] : string.Empty);
            }

            if (cleaned.Length > 700)
            {
                cleaned = cleaned[..700] + "...";
            }

            return cleaned;
        }

        private static bool ApplyGroupLevelResources(IReadOnlyList<Transaction> group, SimulateUnnamedResourcesAccessed accessed)
        {
            var calls = group.OfType<ApplicationCallTransaction>().ToList();
            if (calls.Count == 0)
            {
                return false;
            }

            var changed = false;

            // A box belongs to one application: reference it from a call to that application (so it is addressed with the app-0 shorthand).
            foreach (var box in accessed.Boxes ?? Array.Empty<BoxReference>())
            {
                var holder = calls.FirstOrDefault(c => ((c as ApplicationNoopTransaction)?.ApplicationId ?? 0) == box.App) ?? calls[0];
                changed |= ApplyUnnamedResources(holder, new SimulateUnnamedResourcesAccessed { Boxes = new List<BoxReference> { box } });
            }

            // Everything else is shared by the whole group; the first call that can carry it does.
            var rest = new SimulateUnnamedResourcesAccessed
            {
                Accounts = accessed.Accounts,
                Apps = accessed.Apps,
                Assets = accessed.Assets,
                AssetHoldings = accessed.AssetHoldings,
                AppLocals = accessed.AppLocals,
                ExtraBoxRefs = accessed.ExtraBoxRefs
            };
            if (HasAny(rest))
            {
                var holder = calls.FirstOrDefault(c => c is not ApplicationCreateTransaction) ?? calls[0];
                changed |= ApplyUnnamedResources(holder, rest);
            }

            return changed;
        }

        private static bool ApplyUnnamedResources(ApplicationCallTransaction tx, SimulateUnnamedResourcesAccessed? accessed)
        {
            if (!HasAny(accessed))
            {
                return false;
            }

            var changed = false;
            var calledApp = (tx as ApplicationNoopTransaction)?.ApplicationId ?? 0;

            var accounts = new List<Address>(tx.Accounts ?? Array.Empty<Address>());
            var apps = new List<ulong>(tx.ForeignApps ?? Array.Empty<ulong>());
            var assets = new List<ulong>(tx.ForeignAssets ?? Array.Empty<ulong>());
            var boxes = new List<BoxRef>(tx.Boxes ?? Array.Empty<BoxRef>());

            void AddAccount(Address address)
            {
                if (!address.Equals(tx.Sender) && !accounts.Contains(address))
                {
                    accounts.Add(address);
                    changed = true;
                }
            }

            void AddApp(ulong appId)
            {
                if (appId != calledApp && !apps.Contains(appId))
                {
                    apps.Add(appId);
                    changed = true;
                }
            }

            void AddAsset(ulong assetId)
            {
                if (!assets.Contains(assetId))
                {
                    assets.Add(assetId);
                    changed = true;
                }
            }

            foreach (var address in accessed!.Accounts ?? Array.Empty<Address>())
            {
                AddAccount(address);
            }

            foreach (var appId in accessed.Apps ?? Array.Empty<ulong>())
            {
                AddApp(appId);
            }

            foreach (var assetId in accessed.Assets ?? Array.Empty<ulong>())
            {
                AddAsset(assetId);
            }

            foreach (var holding in accessed.AssetHoldings ?? Array.Empty<AssetHoldingReference>())
            {
                AddAccount(holding.Account);
                AddAsset(holding.Asset);
            }

            foreach (var local in accessed.AppLocals ?? Array.Empty<ApplicationLocalReference>())
            {
                AddAccount(local.Account);
                AddApp(local.App);
            }

            foreach (var box in accessed.Boxes ?? Array.Empty<BoxReference>())
            {
                // A box of the called application is referenced with app 0; a box of any other app needs that app in the foreign apps too.
                var app = box.App == calledApp ? 0UL : box.App;
                if (app != 0)
                {
                    AddApp(app);
                }

                if (!boxes.Any(b => b.App == app && b.Name.AsSpan().SequenceEqual(box.Name)))
                {
                    boxes.Add(new BoxRef { App = app, Name = box.Name });
                    changed = true;
                }
            }

            // Box reads/writes are budgeted per box reference (1 KiB each): large boxes need extra, empty references.
            for (var i = 0UL; i < (accessed.ExtraBoxRefs ?? 0); i++)
            {
                boxes.Add(new BoxRef { App = 0, Name = Array.Empty<byte>() });
                changed = true;
            }

            if (changed)
            {
                tx.Accounts = accounts;
                tx.ForeignApps = apps;
                tx.ForeignAssets = assets;
                tx.Boxes = boxes;
            }

            return changed;
        }

        private static void AssertWithinReferenceLimits(ApplicationCallTransaction tx)
        {
            var accounts = tx.Accounts?.Count ?? 0;
            var total = accounts + (tx.ForeignApps?.Count ?? 0) + (tx.ForeignAssets?.Count ?? 0) + (tx.Boxes?.Count ?? 0);
            if (accounts > MaxAccounts || total > MaxTotalReferences)
            {
                throw new AlgoSafeException(
                    "TooManyReferences",
                    $"This call touches {accounts} accounts and {total} references in total, more than a single transaction can carry (max {MaxAccounts} accounts, {MaxTotalReferences} references). Split it into smaller proposals (fewer distinct recipients/assets per proposal).");
            }
        }

        private static bool HasAny(SimulateUnnamedResourcesAccessed? accessed) =>
            accessed != null
            && ((accessed.Accounts?.Count ?? 0) > 0
                || (accessed.Apps?.Count ?? 0) > 0
                || (accessed.Assets?.Count ?? 0) > 0
                || (accessed.AssetHoldings?.Count ?? 0) > 0
                || (accessed.AppLocals?.Count ?? 0) > 0
                || (accessed.Boxes?.Count ?? 0) > 0
                || (accessed.ExtraBoxRefs ?? 0) > 0);

        /// <summary>Counts every inner transaction under a top-level transaction's execution trace (recursively).</summary>
        private static int CountInner(SimulationTransactionExecTrace? trace)
        {
            if (trace?.InnerTrace == null)
            {
                return 0;
            }

            return trace.InnerTrace.Sum(inner => 1 + CountInner(inner));
        }
    }
}
