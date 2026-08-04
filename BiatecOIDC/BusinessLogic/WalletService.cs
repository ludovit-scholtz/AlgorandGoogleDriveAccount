using BiatecOIDC.Helper;
using BiatecSelfCustodyCore.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Repository;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IWalletService"/>
    public class WalletService : IWalletService
    {
        private readonly IDriveService _driveService;
        private readonly ISpendingLimitService _spendingLimitService;
        private readonly IAssetValuationService _valuationService;
        private readonly IBitcoinValuationService _bitcoinValuationService;
        private readonly ICloudAccountRepository _accountRepository;
        private readonly ILogger<WalletService> _logger;

        public WalletService(
            IDriveService driveService,
            ISpendingLimitService spendingLimitService,
            IAssetValuationService valuationService,
            IBitcoinValuationService bitcoinValuationService,
            ICloudAccountRepository accountRepository,
            ILogger<WalletService> logger)
        {
            _driveService = driveService;
            _spendingLimitService = spendingLimitService;
            _valuationService = valuationService;
            _bitcoinValuationService = bitcoinValuationService;
            _accountRepository = accountRepository;
            _logger = logger;
        }

        public async Task<IReadOnlyList<byte[]>> SignTransactionGroupAsync(string email, string provider, IReadOnlyList<byte[]> transactionsMsgPack, string? accessToken, string? seedAddress = null, int slot = 0, bool applySpendingLimits = true)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }

            if (transactionsMsgPack == null || transactionsMsgPack.Count == 0)
            {
                throw new ArgumentException("At least one transaction is required.", nameof(transactionsMsgPack));
            }

            // Resolved once, up front, so the spending-limit check and every actual signing call below
            // agree on the same identity even if the vault's primary seed changes mid-request.
            var resolvedAddress = await _accountRepository.ResolveSeedAddressAsync(email, provider, seedAddress, accessToken);

            // Decode every transaction up front (cheap, purely local) before doing any network calls -
            // an undecodable transaction should fail fast, not after a round-trip to the router.
            var infos = transactionsMsgPack.Select(AlgorandTransactionInspector.Inspect).ToList();

            // Price every payment/asset-transfer in the group via the Biatec Router, and total it up -
            // every such transaction is subject to the spending limit, so an unpriceable asset fails the
            // whole request (AssetValuationException propagates) rather than being silently skipped.
            // Skipped entirely off Algorand mainnet (applySpendingLimits: false, set by WalletController
            // from the resolved network's genesis id) - the Biatec Router that prices every asset here is
            // only deployed on mainnet, so pricing (and therefore the spending limit it feeds) is simply
            // not a thing that exists yet for testnet/Voi/Aramid/etc. Trying anyway would fail every such
            // transfer closed with a confusing "Unable to determine the USD value..." error, not because
            // anything is actually wrong with the transfer.
            var now = DateTimeOffset.UtcNow;
            var ledgerEntries = new List<SpendingLedgerEntry>();
            var totalUsd = 0m;

            if (applySpendingLimits)
            {
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
                        Kind = info.Kind.ToString(),
                        SeedAddress = resolvedAddress,
                        Slot = slot
                    });
                }

                // Check the whole group's total spend against the caller's global and address-specific
                // daily/weekly/monthly limits before signing any of it - signing has no rollback, so a group
                // that would exceed a limit must never partially sign.
                if (totalUsd > 0m)
                {
                    await _spendingLimitService.EnsureWithinLimitsAsync(email, provider, accessToken, totalUsd, resolvedAddress, slot);
                }
            }

            var signed = new List<byte[]>(transactionsMsgPack.Count);
            foreach (var txMsgPack in transactionsMsgPack)
            {
                signed.Add(await _driveService.SignTransactionAsync(email, txMsgPack, provider, accessToken, resolvedAddress, slot));
            }

            if (ledgerEntries.Count > 0)
            {
                await _spendingLimitService.RecordSpendAsync(email, provider, accessToken, ledgerEntries);
            }

            _logger.LogInformation("Signed a {Count}-transaction group for {Email}, totaling {TotalUsd:0.####} USD.", signed.Count, email, totalUsd);
            return signed;
        }

        public async Task<IReadOnlyList<byte[]>> SignEvmTransactionGroupAsync(string email, string provider, IReadOnlyList<EvmUnsignedTransaction> unsignedTransactions, string? accessToken, string? seedAddress = null, int slot = 0)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }

            if (unsignedTransactions == null || unsignedTransactions.Count == 0)
            {
                throw new ArgumentException("At least one transaction is required.", nameof(unsignedTransactions));
            }

            var signed = new List<byte[]>(unsignedTransactions.Count);
            foreach (var tx in unsignedTransactions)
            {
                signed.Add(await _driveService.SignEvmTransactionAsync(email, tx, provider, accessToken, seedAddress, slot));
            }

            _logger.LogInformation("Signed a {Count}-transaction EVM group for {Email}.", signed.Count, email);
            return signed;
        }

        public async Task<byte[]> SignBitcoinTransactionGroupAsync(string email, string provider, BitcoinChainFamily family, BitcoinUnsignedTransaction transaction, string? accessToken, string? seedAddress = null, int slot = 0)
        {
            if (string.IsNullOrWhiteSpace(email))
            {
                throw new ArgumentException("Email is required.", nameof(email));
            }

            if (transaction == null || transaction.Inputs.Count == 0)
            {
                throw new ArgumentException("At least one input is required.", nameof(transaction));
            }

            // Resolved once, up front, so the spending-limit check and the actual signing call below agree
            // on the same identity even if the vault's primary seed changes mid-request.
            var resolvedAddress = await _accountRepository.ResolveSeedAddressAsync(email, provider, seedAddress, accessToken);

            var spendSatoshis = transaction.Outputs.Where(o => !o.IsChange).Sum(o => o.AmountSatoshis);
            var totalUsd = spendSatoshis > 0 ? await _bitcoinValuationService.GetUsdValueAsync(family, spendSatoshis) : 0m;

            if (totalUsd > 0m)
            {
                await _spendingLimitService.EnsureWithinLimitsAsync(email, provider, accessToken, totalUsd, resolvedAddress, slot);
            }

            var signed = await _driveService.SignBitcoinTransactionAsync(email, family, transaction, provider, accessToken, resolvedAddress, slot);

            if (totalUsd > 0m)
            {
                await _spendingLimitService.RecordSpendAsync(email, provider, accessToken, new[]
                {
                    new SpendingLedgerEntry
                    {
                        TimestampUtc = DateTimeOffset.UtcNow,
                        AmountUsd = totalUsd,
                        AssetId = 0,
                        Kind = family.ToString(),
                        SeedAddress = resolvedAddress,
                        Slot = slot
                    }
                });
            }

            _logger.LogInformation("Signed a {Family} transaction for {Email}, totaling {TotalUsd:0.####} USD.", family, email, totalUsd);
            return signed;
        }
    }
}
