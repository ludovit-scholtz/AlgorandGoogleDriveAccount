using BiatecOIDC.Helper;
using BiatecSelfCustodyCore.BusinessLogic;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IWalletService"/>
    public class WalletService : IWalletService
    {
        private readonly IDriveService _driveService;
        private readonly ISpendingLimitService _spendingLimitService;
        private readonly IAssetValuationService _valuationService;
        private readonly ILogger<WalletService> _logger;

        public WalletService(
            IDriveService driveService,
            ISpendingLimitService spendingLimitService,
            IAssetValuationService valuationService,
            ILogger<WalletService> logger)
        {
            _driveService = driveService;
            _spendingLimitService = spendingLimitService;
            _valuationService = valuationService;
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

            // Decode every transaction up front (cheap, purely local) before doing any network calls -
            // an undecodable transaction should fail fast, not after a round-trip to the router.
            var infos = transactionsMsgPack.Select(AlgorandTransactionInspector.Inspect).ToList();

            // Price every payment/asset-transfer in the group via the Biatec Router, and total it up -
            // every such transaction is subject to the spending limit, so an unpriceable asset fails the
            // whole request (AssetValuationException propagates) rather than being silently skipped.
            var now = DateTimeOffset.UtcNow;
            var ledgerEntries = new List<SpendingLedgerEntry>();
            var totalUsd = 0m;

            foreach (var info in infos)
            {
                if (info.Kind is not (AlgorandTransactionKind.Payment or AlgorandTransactionKind.AssetTransfer))
                {
                    continue;
                }

                var usdValue = await _valuationService.GetUsdValueAsync(info.AssetId, info.Amount);
                totalUsd += usdValue;
                ledgerEntries.Add(new SpendingLedgerEntry
                {
                    TimestampUtc = now,
                    AmountUsd = usdValue,
                    AssetId = info.AssetId,
                    Kind = info.Kind.ToString()
                });
            }

            // Check the whole group's total spend against the caller's daily/weekly/monthly limits before
            // signing any of it - signing has no rollback, so a group that would exceed a limit must never
            // partially sign.
            if (totalUsd > 0m)
            {
                await _spendingLimitService.EnsureWithinLimitsAsync(email, provider, accessToken, totalUsd);
            }

            var signed = new List<byte[]>(transactionsMsgPack.Count);
            foreach (var txMsgPack in transactionsMsgPack)
            {
                signed.Add(await _driveService.SignTransactionAsync(email, txMsgPack, provider, accessToken));
            }

            if (ledgerEntries.Count > 0)
            {
                await _spendingLimitService.RecordSpendAsync(email, provider, accessToken, ledgerEntries);
            }

            _logger.LogInformation("Signed a {Count}-transaction group for {Email}, totaling {TotalUsd:0.####} USD.", signed.Count, email, totalUsd);
            return signed;
        }
    }
}
