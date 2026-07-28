using BiatecOIDC.Helper;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="IWalletService"/> when a payment or asset transfer in a requested signing
    /// group exceeds the caller's configured spending limit. Caught by <c>WalletController</c> and
    /// mapped to a 403 response - never a 500, since this is an expected, caller-correctable outcome.
    /// </summary>
    public sealed class SpendingLimitExceededException : Exception
    {
        public AlgorandTransactionKind Kind { get; }
        public ulong Amount { get; }
        public ulong MaxAmountPerTransaction { get; }

        public SpendingLimitExceededException(AlgorandTransactionKind kind, ulong amount, ulong maxAmountPerTransaction)
            : base($"{kind} amount {amount} exceeds the configured spending limit of {maxAmountPerTransaction} per transaction.")
        {
            Kind = kind;
            Amount = amount;
            MaxAmountPerTransaction = maxAmountPerTransaction;
        }
    }
}
