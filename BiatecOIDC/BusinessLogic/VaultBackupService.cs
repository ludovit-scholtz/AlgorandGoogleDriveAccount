using System.Text.Json;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using StackExchange.Redis;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IVaultBackupService"/>
    public sealed class VaultBackupService : IVaultBackupService
    {
        private const string PendingPrefix = "vaultbackup:pending:";
        private const string LinkedPrefix = "vaultbackup:linked:";
        private static readonly TimeSpan PendingLifetime = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan LinkedLifetime = TimeSpan.FromMinutes(10);

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IConnectionMultiplexer _redis;
        private readonly ICloudStorageProviderCatalog _providerCatalog;
        private readonly ICloudAccountRepository _accountRepository;
        private readonly ILogger<VaultBackupService> _logger;

        public VaultBackupService(
            IConnectionMultiplexer redis,
            ICloudStorageProviderCatalog providerCatalog,
            ICloudAccountRepository accountRepository,
            ILogger<VaultBackupService> logger)
        {
            _redis = redis;
            _providerCatalog = providerCatalog;
            _accountRepository = accountRepository;
            _logger = logger;
        }

        public async Task<string> StartAsync(string email, string primaryProvider, string targetProvider)
        {
            if (string.Equals(primaryProvider, targetProvider, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The backup target must be a different provider than your primary one.");
            }

            var resolved = _providerCatalog.All.FirstOrDefault(p => string.Equals(p.Name, targetProvider, StringComparison.OrdinalIgnoreCase));
            if (resolved == null || !resolved.IsConfigured)
            {
                throw new InvalidOperationException($"'{targetProvider}' is not a recognized, configured backup target.");
            }

            var linkId = GenerateLinkId();
            var pending = new PendingVaultBackup(email, resolved.Name);
            await _redis.GetDatabase().StringSetAsync(PendingPrefix + linkId, JsonSerializer.Serialize(pending, JsonOptions), PendingLifetime);
            return linkId;
        }

        public async Task<PendingVaultBackup?> GetPendingAsync(string linkId)
        {
            var json = await _redis.GetDatabase().StringGetAsync(PendingPrefix + linkId);
            if (json.IsNullOrEmpty)
            {
                return null;
            }

            return JsonSerializer.Deserialize<PendingVaultBackup>((string)json!, JsonOptions);
        }

        public async Task<(bool Success, string? Error)> HandleCallbackAsync(string linkId, string code, string redirectUri)
        {
            var pending = await GetPendingAsync(linkId);
            if (pending == null)
            {
                return (false, "This backup link has expired or was never started. Please start again.");
            }

            var provider = _providerCatalog.Resolve(pending.TargetProvider);
            var accessToken = await provider.ExchangeAuthorizationCodeAsync(code, redirectUri);
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return (false, $"Unable to complete authorization with {provider.DisplayName}. Please try again.");
            }

            if (!await provider.HasWriteAccessAsync(accessToken))
            {
                return (false, $"{provider.DisplayName} did not grant storage-write access, which is required to store your backup.");
            }

            var linked = new LinkedVaultBackup(pending.Email, pending.TargetProvider, accessToken);
            await _redis.GetDatabase().StringSetAsync(LinkedPrefix + linkId, JsonSerializer.Serialize(linked, JsonOptions), LinkedLifetime);
            await _redis.GetDatabase().KeyDeleteAsync(PendingPrefix + linkId);
            return (true, null);
        }

        public async Task<(bool Success, string? Error)> CompleteAsync(string email, string primaryProvider, string? primaryAccessToken, string linkId)
        {
            // One-shot: read-and-delete, so the linked target-provider token is never usable twice and
            // never lingers in Redis beyond this single call.
            var db = _redis.GetDatabase();
            var json = await db.StringGetDeleteAsync(LinkedPrefix + linkId);
            if (json.IsNullOrEmpty)
            {
                return (false, "This backup link has expired, was already used, or was never completed. Please start again.");
            }

            var linked = JsonSerializer.Deserialize<LinkedVaultBackup>((string)json!, JsonOptions);
            if (linked == null || !string.Equals(linked.Email, email, StringComparison.Ordinal))
            {
                return (false, "This backup link does not belong to the current caller.");
            }

            if (string.IsNullOrWhiteSpace(primaryAccessToken))
            {
                return (false, "No cached access token is available for your primary provider. Please sign in again.");
            }

            try
            {
                var (fileName, encryptedBytes) = await _accountRepository.GetEncryptedVaultForBackupAsync(email, primaryProvider, primaryAccessToken);
                var targetProvider = _providerCatalog.Resolve(linked.TargetProvider);
                await targetProvider.UploadAsync(fileName, encryptedBytes, linked.TargetProviderAccessToken);

                _logger.LogInformation("Backed up the vault for {Email} from {PrimaryProvider} to {TargetProvider}.", email, primaryProvider, linked.TargetProvider);
                return (true, null);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to back up the vault for {Email} from {PrimaryProvider} to {TargetProvider}.", email, primaryProvider, linked.TargetProvider);
                return (false, "Unable to complete the backup copy. Please try again.");
            }
        }

        private static string GenerateLinkId() => Convert.ToHexString(Guid.NewGuid().ToByteArray()).ToLowerInvariant();

        private sealed record LinkedVaultBackup(string Email, string TargetProvider, string TargetProviderAccessToken);
    }
}
