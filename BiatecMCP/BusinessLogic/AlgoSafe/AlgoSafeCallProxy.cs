using Algorand;
using Algorand.Algod;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Arc56.Generated.ludovit_scholtz.algo_safe.AlgoSafe_e28553fc;
using Arc4 = AVM.ClientGenerator.ABI.ARC4.Types;

namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// The generated <see cref="AlgoSafeProxy"/> with the two calls it mis-encodes rebuilt on raw, correctly ARC-4 encoded arguments
    /// (see <see cref="AlgoSafePayloadCodec.EncodeEnvelopeArray"/>): <c>proposeTransactionGroup</c> and <c>bootstrapGroup</c> both take an
    /// array of dynamic tuples, which the generator concatenates without offsets. Everything else (selectors, app creation, the simple ABI
    /// methods) still comes from the generated client.
    /// </summary>
    public sealed class AlgoSafeCallProxy : AlgoSafeProxy
    {
        // ARC-4 method selectors from the ARC-56 spec (first 4 bytes of the method signature hash).
        private static readonly byte[] ProposeTransactionGroupSelector = [172, 58, 154, 50];
        private static readonly byte[] BootstrapGroupSelector = [20, 168, 255, 186];

        public AlgoSafeCallProxy(DefaultApi defaultApi, ulong appId)
            : base(defaultApi, appId)
        {
        }

        /// <summary><c>proposeTransactionGroup(uint64,(uint64,byte[])[],uint64,bool,uint64)uint64</c>.</summary>
        public Task<List<Transaction>> BuildProposeTransactionGroupAsync(
            ulong groupId,
            IReadOnlyList<AlgoSafePayloadEntry> payload,
            ulong expiryRound,
            bool execute,
            ulong ensureBudgetValue,
            Account sender,
            List<Transaction>? preTransactions = null)
        {
            var groupIdArg = new Arc4.UInt64();
            groupIdArg.From(groupId);
            var expiryArg = new Arc4.UInt64();
            expiryArg.From(expiryRound);
            var executeArg = new Arc4.Bool();
            executeArg.From(execute);
            var budgetArg = new Arc4.UInt64();
            budgetArg.From(ensureBudgetValue);

            return MakeTransactionList(
                new List<object> { ProposeTransactionGroupSelector, groupIdArg, AlgoSafePayloadCodec.EncodeEnvelopeArray(payload), expiryArg, executeArg, budgetArg },
                sender, null, string.Empty, 1000, _tx_transactions: preTransactions);
        }

        /// <summary><c>bootstrapGroup((string,uint64,...),(address,uint64,string)[],uint64)uint64</c>, appended to <paramref name="preTransactions"/> (e.g. the deposit payment).</summary>
        public Task<List<Transaction>> BuildBootstrapGroupAsync(
            byte[] encodedSeed,
            IReadOnlyList<(Address Address, ulong AccountType, string Label)> members,
            ulong ensureBudgetValue,
            Account sender,
            List<Transaction> preTransactions)
        {
            var budgetArg = new Arc4.UInt64();
            budgetArg.From(ensureBudgetValue);

            return MakeTransactionList(
                new List<object> { BootstrapGroupSelector, encodedSeed, AlgoSafePayloadCodec.EncodeMembersArray(members), budgetArg },
                sender, null, string.Empty, 1000, _tx_transactions: preTransactions);
        }
    }
}
