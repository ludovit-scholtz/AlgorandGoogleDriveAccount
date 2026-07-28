using BiatecOIDC.Helper;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Helper;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IWalletService"/>
    public class WalletService : IWalletService
    {
        private readonly IDriveService _driveService;
        private readonly ISpendingLimitService _spendingLimitService;
        private readonly ILogger<WalletService> _logger;

        public WalletService(IDriveService driveService, ISpendingLimitService spendingLimitService, ILogger<WalletService> logger)
        {
            _driveService = driveService;
            _spendingLimitService = spendingLimitService;
            _logger = logger;
        }

        public async Task<IReadOnlyList<byte[]>> SignTransactionGroupAsync(string email, string provider, IReadOnlyList<byte[]> transactionsMsgPack, string? accessToken)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }

            if (transactionsMsgPack == null || transactionsMsgPack.Count == 0)
            {
                throw new ArgumentException("At least one transaction is required.", nameof(transactionsMsgPack));
            }

            var maxAmountPerTransaction = await _spendingLimitService.GetMaxAmountPerTransactionAsync(email);

            // Validate every transaction in the group up front, before signing any of them - signing has
            // no rollback, so a group with one over-limit transaction must never partially sign the rest.
            foreach (var txMsgPack in transactionsMsgPack)
            {
                var info = AlgorandTransactionInspector.Inspect(txMsgPack);

                if (info.Kind is not (AlgorandTransactionKind.Payment or AlgorandTransactionKind.AssetTransfer))
                {
                    continue;
                }

                if (TransferPolicy.ExceedsMaxAmount(info.Amount, maxAmountPerTransaction))
                {
                    _logger.LogWarning(
                        "Rejecting sign request for {Email}: {Kind} amount {Amount} exceeds spending limit {MaxAmount}.",
                        email, info.Kind, info.Amount, maxAmountPerTransaction);
                    throw new SpendingLimitExceededException(info.Kind, info.Amount, maxAmountPerTransaction);
                }
            }

            var signed = new List<byte[]>(transactionsMsgPack.Count);
            foreach (var txMsgPack in transactionsMsgPack)
            {
                signed.Add(await _driveService.SignTransactionAsync(email, txMsgPack, provider, accessToken));
            }

            _logger.LogInformation("Signed a {Count}-transaction group for {Email}.", signed.Count, email);
            return signed;
        }
    }
}
