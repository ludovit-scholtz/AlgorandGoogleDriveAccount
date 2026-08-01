using BiatecSelfCustodyCore.Helper;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Covers <see cref="CloudAccountRepository"/>'s AES key-ring rotation behavior: the account file's name
    /// is derived from the key generation that encrypted it (<c>%AESID%</c>), so a file found only under a
    /// historical generation's name must be migrated (re-encrypted + re-uploaded under the active generation,
    /// old file deleted) rather than treated as missing (which would silently create a brand-new account and
    /// orphan the user's real mnemonic - the bug this feature fixes).
    /// </summary>
    [TestFixture]
    public class CloudAccountRepositoryTests
    {
        private const string TestEmail = "user@example.com";
        private const string ActiveKeyId = "gen-2";
        private const string HistoricalKeyId = "gen-1";

        private PersistentFakeCloudStorageProvider _fakeProvider = null!;
        private Mock<ICloudStorageProviderCatalog> _mockCatalog = null!;
        private Mock<IOptionsMonitor<Configuration>> _mockConfig = null!;
        private Mock<IOptionsMonitor<AesOptions>> _mockAesOptions = null!;
        private AesOptions _aesOptionsValue = null!;

        [SetUp]
        public void SetUp()
        {
            _fakeProvider = new PersistentFakeCloudStorageProvider("ambient-token");
            _mockCatalog = new Mock<ICloudStorageProviderCatalog>();
            _mockCatalog.Setup(c => c.Resolve(It.IsAny<string>())).Returns(_fakeProvider);

            _mockConfig = new Mock<IOptionsMonitor<Configuration>>();
            _mockConfig.Setup(c => c.CurrentValue).Returns(new Configuration { StorageFileName = "AVMAccount.%AESID%.dat" });

            _aesOptionsValue = BuildAesOptions(ActiveKeyId, MakeKeyEntry(ActiveKeyId, 2), MakeKeyEntry(HistoricalKeyId, 1));
            _mockAesOptions = new Mock<IOptionsMonitor<AesOptions>>();
            _mockAesOptions.Setup(a => a.CurrentValue).Returns(() => _aesOptionsValue);
        }

        private static AesKeyRingEntry MakeKeyEntry(string keyId, byte fill) => new()
        {
            KeyId = keyId,
            Key = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()),
            IV = Convert.ToBase64String(Enumerable.Repeat(fill, 16).ToArray())
        };

        private static AesOptions BuildAesOptions(string activeKeyId, params AesKeyRingEntry[] keys) => new()
        {
            ActiveKeyId = activeKeyId,
            Keys = keys.ToList()
        };

        private CloudAccountRepository CreateRepository(string? environmentName = null)
        {
            var mockEnvironment = new Mock<IHostEnvironment>();
            mockEnvironment.Setup(e => e.EnvironmentName).Returns(environmentName ?? Environments.Development);
            return new CloudAccountRepository(_mockCatalog.Object, _mockConfig.Object, _mockAesOptions.Object, mockEnvironment.Object, new Mock<ILogger<CloudAccountRepository>>().Object);
        }

        private static string ActiveFileName(AesKeyRingEntry key) =>
            "AVMAccount." + AesEncryptionHelper.MakeAesId(AesKeyRingResolver.KeyBytes(key), AesKeyRingResolver.IvBytes(key)) + ".dat";

        [Test]
        public async Task LoadAccountAsync_NoExistingFile_CreatesNewAccountEncryptedUnderActiveKey()
        {
            var repository = CreateRepository();

            var account = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "explicit-token");

            Assert.That(account, Is.Not.Null);
            var activeFileName = ActiveFileName(_aesOptionsValue.Keys.First(k => k.KeyId == ActiveKeyId));
            Assert.That(_fakeProvider.Files.ContainsKey(activeFileName), Is.True);
        }

        [Test]
        public async Task LoadAccountAsync_FileAlreadyUnderActiveKey_DoesNotReuploadOrDelete()
        {
            var repository = CreateRepository();
            var firstLoad = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            var uploadCountAfterCreate = _fakeProvider.UploadCount;

            var secondLoad = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            Assert.That(secondLoad.Address, Is.EqualTo(firstLoad.Address));
            Assert.That(_fakeProvider.UploadCount, Is.EqualTo(uploadCountAfterCreate)); // fast path - no re-encrypt needed
            Assert.That(_fakeProvider.DeleteCount, Is.EqualTo(0));
        }

        [Test]
        public async Task LoadAccountAsync_FileUnderHistoricalKey_MigratesToActiveKeyAndDeletesOldFile()
        {
            var historicalKey = _aesOptionsValue.Keys.First(k => k.KeyId == HistoricalKeyId);
            var activeKey = _aesOptionsValue.Keys.First(k => k.KeyId == ActiveKeyId);
            var historicalFileName = ActiveFileName(historicalKey);
            var activeFileName = ActiveFileName(activeKey);

            var mnemonicBytes = System.Text.Encoding.UTF8.GetBytes(new Algorand.Algod.Model.Account().ToMnemonic());
            var encryptedUnderHistoricalKey = AesEncryptionHelper.Encrypt(mnemonicBytes, AesKeyRingResolver.KeyBytes(historicalKey), AesKeyRingResolver.IvBytes(historicalKey), TestEmail);
            _fakeProvider.Files[historicalFileName] = encryptedUnderHistoricalKey;

            var repository = CreateRepository();
            var account = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            Assert.That(account, Is.Not.Null);
            // Migrated: active-key file now exists, historical-key file was deleted.
            Assert.That(_fakeProvider.Files.ContainsKey(activeFileName), Is.True);
            Assert.That(_fakeProvider.Files.ContainsKey(historicalFileName), Is.False);

            // The migrated content really is the same mnemonic, just re-encrypted under the active key.
            var decrypted = AesEncryptionHelper.Decrypt(_fakeProvider.Files[activeFileName], AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey), TestEmail);
            Assert.That(decrypted, Is.EqualTo(mnemonicBytes));

            // A second load now hits the fast path - no further migration/deletion.
            var uploadCountAfterMigration = _fakeProvider.UploadCount;
            var deleteCountAfterMigration = _fakeProvider.DeleteCount;
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            Assert.That(_fakeProvider.UploadCount, Is.EqualTo(uploadCountAfterMigration));
            Assert.That(_fakeProvider.DeleteCount, Is.EqualTo(deleteCountAfterMigration));
        }

        [Test]
        public void LoadAccountAsync_NoAccessToken_ThrowsUnauthorizedAccessException()
        {
            _fakeProvider.AmbientAccessToken = null;
            var repository = CreateRepository();

            Assert.That(async () => await repository.LoadAccountAsync(TestEmail, 0, "Fake"),
                Throws.InstanceOf<UnauthorizedAccessException>());
        }

        [Test]
        public void Construction_ActiveKeyMissing_InProduction_Throws()
        {
            _aesOptionsValue = BuildAesOptions(string.Empty);

            Assert.Throws<InvalidOperationException>(() => CreateRepository(Environments.Production));
        }

        [Test]
        public void Construction_ActiveKeyMissing_InDevelopment_DoesNotThrow()
        {
            _aesOptionsValue = BuildAesOptions(string.Empty);

            Assert.DoesNotThrow(() => CreateRepository(Environments.Development));
        }

        /// <summary>
        /// In-memory <see cref="ICloudStorageProvider"/> test double that actually persists uploaded bytes
        /// (and honors deletes) across calls, with call counters so migration behavior can be asserted.
        /// </summary>
        private sealed class PersistentFakeCloudStorageProvider : ICloudStorageProvider
        {
            public PersistentFakeCloudStorageProvider(string? ambientAccessToken)
            {
                AmbientAccessToken = ambientAccessToken;
            }

            public Dictionary<string, byte[]> Files { get; } = new();
            public string? AmbientAccessToken { get; set; }
            public int UploadCount { get; private set; }
            public int DeleteCount { get; private set; }

            public string Name => "Fake";
            public string DisplayName => "Fake";
            public string RequiredScope => "fake-scope";
            public bool IsConfigured => true;

            public Task<byte[]?> TryDownloadAsync(string fileName, string accessToken) =>
                Task.FromResult(Files.TryGetValue(fileName, out var content) ? content : null);

            public Task UploadAsync(string fileName, byte[] content, string accessToken)
            {
                Files[fileName] = content;
                UploadCount++;
                return Task.CompletedTask;
            }

            public Task DeleteAsync(string fileName, string accessToken)
            {
                Files.Remove(fileName);
                DeleteCount++;
                return Task.CompletedTask;
            }

            public Task<bool> HasWriteAccessAsync(string accessToken) => Task.FromResult(true);
            public Task<string?> GetAmbientAccessTokenAsync() => Task.FromResult(AmbientAccessToken);
            public Task<string?> GetAmbientRefreshTokenAsync() => Task.FromResult<string?>(null);
            public Task<ProviderTokenRefreshResult?> RefreshAccessTokenAsync(string refreshToken) => Task.FromResult<ProviderTokenRefreshResult?>(null);
        }
    }
}
