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

            // Simulates a file predating BOTH the AES key-ring feature AND the multi-seed vault format:
            // the legacy content is just the raw mnemonic bytes, not a serialized SeedVault.
            var mnemonic = new Algorand.Algod.Model.Account().ToMnemonic();
            var mnemonicBytes = System.Text.Encoding.UTF8.GetBytes(mnemonic);
            var encryptedUnderHistoricalKey = AesEncryptionHelper.Encrypt(mnemonicBytes, AesKeyRingResolver.KeyBytes(historicalKey), AesKeyRingResolver.IvBytes(historicalKey), TestEmail);
            _fakeProvider.Files[historicalFileName] = encryptedUnderHistoricalKey;

            var repository = CreateRepository();
            var account = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            Assert.That(account, Is.Not.Null);
            // Migrated: active-key file now exists, historical-key file was deleted.
            Assert.That(_fakeProvider.Files.ContainsKey(activeFileName), Is.True);
            Assert.That(_fakeProvider.Files.ContainsKey(historicalFileName), Is.False);

            // The migrated content is a single-seed vault wrapping the same mnemonic, re-encrypted under
            // the active key (both the AES-rotation migration and the legacy-format-to-vault migration
            // happen together, in one pass).
            var decrypted = AesEncryptionHelper.Decrypt(_fakeProvider.Files[activeFileName], AesKeyRingResolver.KeyBytes(activeKey), AesKeyRingResolver.IvBytes(activeKey), TestEmail);
            var vault = System.Text.Json.JsonSerializer.Deserialize<SeedVault>(decrypted, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            Assert.That(vault!.Seeds, Has.Count.EqualTo(1));
            Assert.That(vault.Seeds[0].Mnemonic, Is.EqualTo(mnemonic));
            Assert.That(vault.Seeds[0].IsPrimary, Is.True);

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

        // ───────────────────────── Multi-seed vault ─────────────────────────

        [Test]
        public async Task ListSeedsAsync_FirstEverLoad_ReturnsExactlyOnePrimarySeed()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token"); // creates the vault

            var seeds = await repository.ListSeedsAsync(TestEmail, "Fake", "token");

            Assert.That(seeds, Has.Count.EqualTo(1));
            Assert.That(seeds[0].IsPrimary, Is.True);
        }

        [Test]
        public async Task CreateSeedAsync_OnExistingVault_AppendsNonPrimarySeedWithoutRemovingTheOld()
        {
            var repository = CreateRepository();
            var firstAccount = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token"); // creates the first (primary) seed

            var newSeed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            Assert.That(newSeed.IsPrimary, Is.False, "a second seed must not silently become primary");
            var seeds = await repository.ListSeedsAsync(TestEmail, "Fake", "token");
            Assert.That(seeds, Has.Count.EqualTo(2));
            Assert.That(seeds.Count(s => s.IsPrimary), Is.EqualTo(1));

            // The primary seed (and therefore LoadAccountAsync's derived account) is unaffected by minting a spare key.
            var accountAfterCreate = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            Assert.That(accountAfterCreate.Address.EncodeAsString(), Is.EqualTo(firstAccount.Address.EncodeAsString()));
        }

        [Test]
        public async Task CreateSeedAsync_AsTheVeryFirstSeed_IsAutomaticallyPrimary()
        {
            var repository = CreateRepository();

            var seed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            Assert.That(seed.IsPrimary, Is.True);
        }

        [Test]
        public async Task SwitchPrimarySeedAsync_ValidAddress_MakesItPrimaryAndDemotesTheOthers()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            var newSeed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            await repository.SwitchPrimarySeedAsync(TestEmail, "Fake", newSeed.SeedAddress, "token");

            var seeds = await repository.ListSeedsAsync(TestEmail, "Fake", "token");
            Assert.That(seeds.Single(s => s.IsPrimary).SeedAddress, Is.EqualTo(newSeed.SeedAddress));

            // LoadAccountAsync now derives from the newly-primary seed - a materially different account.
            var accountAfterSwitch = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            Assert.That(accountAfterSwitch.Address.EncodeAsString(), Is.EqualTo(newSeed.SeedAddress));
        }

        [Test]
        public void SwitchPrimarySeedAsync_UnknownAddress_ThrowsInvalidOperationException()
        {
            var repository = CreateRepository();

            Assert.That(async () =>
                {
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
                    await repository.SwitchPrimarySeedAsync(TestEmail, "Fake", "NOTAREALADDRESS", "token");
                },
                Throws.InvalidOperationException);
        }

        [Test]
        public async Task GetEncryptedVaultForBackupAsync_ReturnsTheSameBytesStoredUnderTheActiveFileName()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var (fileName, encryptedBytes) = await repository.GetEncryptedVaultForBackupAsync(TestEmail, "Fake", "token");

            Assert.That(_fakeProvider.Files.ContainsKey(fileName), Is.True);
            Assert.That(encryptedBytes, Is.EqualTo(_fakeProvider.Files[fileName]));
        }

        // ───────────────────────── Address selection (multi-address signing) ─────────────────────────

        [Test]
        public async Task LoadAccountAsync_WithPrimaryAddressOfNonPrimarySeed_DerivesFromThatSeedNotTheCurrentPrimary()
        {
            var repository = CreateRepository();
            var primaryAccount = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            var secondSeed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            var account = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token", secondSeed.SeedAddress);

            Assert.That(account.Address.EncodeAsString(), Is.EqualTo(secondSeed.SeedAddress));
            Assert.That(account.Address.EncodeAsString(), Is.Not.EqualTo(primaryAccount.Address.EncodeAsString()));
        }

        [Test]
        public void LoadAccountAsync_WithUnknownPrimaryAddress_ThrowsInvalidOperationException()
        {
            var repository = CreateRepository();

            Assert.That(async () =>
                {
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token", "NOTAREALADDRESS");
                },
                Throws.InvalidOperationException);
        }

        [Test]
        public async Task DeriveAddressAsync_NullPrimaryAddress_DerivesFromCurrentPrimarySeed()
        {
            var repository = CreateRepository();
            var primaryAccount = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var derived = await repository.DeriveAddressAsync(TestEmail, "Fake", null, 0, "token");

            Assert.That(derived, Is.EqualTo(primaryAccount.Address.EncodeAsString()));
        }

        [Test]
        public async Task DeriveAddressAsync_NonZeroSlot_DiffersFromSlotZero()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var slot0 = await repository.DeriveAddressAsync(TestEmail, "Fake", null, 0, "token");
            var slot1 = await repository.DeriveAddressAsync(TestEmail, "Fake", null, 1, "token");

            Assert.That(slot1, Is.Not.EqualTo(slot0));
        }

        [Test]
        public async Task DeriveAddressAsync_SpecificSeed_DerivesFromThatSeed()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            var secondSeed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            var derived = await repository.DeriveAddressAsync(TestEmail, "Fake", secondSeed.SeedAddress, 0, "token");

            Assert.That(derived, Is.EqualTo(secondSeed.SeedAddress));
        }

        [Test]
        public void DeriveAddressAsync_UnknownPrimaryAddress_ThrowsInvalidOperationException()
        {
            var repository = CreateRepository();

            Assert.That(async () =>
                {
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
                    await repository.DeriveAddressAsync(TestEmail, "Fake", "NOTAREALADDRESS", 0, "token");
                },
                Throws.InvalidOperationException);
        }

        [Test]
        public async Task DeriveEvmAddressAsync_DiffersFromAlgorandAddressForSameSeedAndSlot()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var algorandAddress = await repository.DeriveAddressAsync(TestEmail, "Fake", null, 0, "token");
            var evmAddress = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 0, "token");

            Assert.That(evmAddress, Does.StartWith("0x"));
            Assert.That(evmAddress, Is.Not.EqualTo(algorandAddress));
        }

        [Test]
        public async Task DeriveEvmAddressAsync_IsDeterministicForTheSameSeedAndSlot()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var first = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 0, "token");
            var second = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 0, "token");

            Assert.That(second, Is.EqualTo(first));
        }

        [Test]
        public async Task DeriveEvmAddressAsync_NonZeroSlot_DiffersFromSlotZero()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var slot0 = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 0, "token");
            var slot1 = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 1, "token");

            Assert.That(slot1, Is.Not.EqualTo(slot0));
        }

        [Test]
        public async Task DeriveEvmAddressAsync_SpecificSeed_DerivesFromThatSeed()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            var secondSeed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            var derivedFromPrimary = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 0, "token");
            var derivedFromSecond = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", secondSeed.SeedAddress, 0, "token");

            Assert.That(derivedFromSecond, Is.Not.EqualTo(derivedFromPrimary));
        }

        [Test]
        public void DeriveEvmAddressAsync_UnknownPrimaryAddress_ThrowsInvalidOperationException()
        {
            var repository = CreateRepository();

            Assert.That(async () =>
                {
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
                    await repository.DeriveEvmAddressAsync(TestEmail, "Fake", "NOTAREALADDRESS", 0, "token");
                },
                Throws.InvalidOperationException);
        }

        [Test]
        public async Task LoadEvmAccountAsync_ReturnsTheSigningKeyForTheDerivedEvmAddress()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var evmAddress = await repository.DeriveEvmAddressAsync(TestEmail, "Fake", null, 0, "token");
            var key = await repository.LoadEvmAccountAsync(TestEmail, 0, "Fake", "token");

            Assert.That(key.GetPublicAddress(), Is.EqualTo(evmAddress));
        }

        [Test]
        public async Task LoadEvmAccountAsync_NonZeroSlot_DiffersFromSlotZero()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var slot0 = await repository.LoadEvmAccountAsync(TestEmail, 0, "Fake", "token");
            var slot1 = await repository.LoadEvmAccountAsync(TestEmail, 1, "Fake", "token");

            Assert.That(slot1.GetPublicAddress(), Is.Not.EqualTo(slot0.GetPublicAddress()));
        }

        [Test]
        public void LoadEvmAccountAsync_UnknownPrimaryAddress_ThrowsInvalidOperationException()
        {
            var repository = CreateRepository();

            Assert.That(async () =>
                {
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
                    await repository.LoadEvmAccountAsync(TestEmail, 0, "Fake", "token", "NOTAREALADDRESS");
                },
                Throws.InvalidOperationException);
        }

        [Test]
        public async Task ResolveSeedAddressAsync_NullSelector_ReturnsCurrentPrimarySeedAddress()
        {
            var repository = CreateRepository();
            var primaryAccount = await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");

            var resolved = await repository.ResolveSeedAddressAsync(TestEmail, "Fake", null, "token");

            Assert.That(resolved, Is.EqualTo(primaryAccount.Address.EncodeAsString()));
        }

        [Test]
        public async Task ResolveSeedAddressAsync_KnownNonPrimarySeed_ReturnsThatAddress()
        {
            var repository = CreateRepository();
            await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
            var secondSeed = await repository.CreateSeedAsync(TestEmail, "Fake", "token");

            var resolved = await repository.ResolveSeedAddressAsync(TestEmail, "Fake", secondSeed.SeedAddress, "token");

            Assert.That(resolved, Is.EqualTo(secondSeed.SeedAddress));
        }

        [Test]
        public void ResolveSeedAddressAsync_UnknownSelector_ThrowsInvalidOperationException()
        {
            var repository = CreateRepository();

            Assert.That(async () =>
                {
                    await repository.LoadAccountAsync(TestEmail, 0, "Fake", "token");
                    await repository.ResolveSeedAddressAsync(TestEmail, "Fake", "NOTAREALADDRESS", "token");
                },
                Throws.InvalidOperationException);
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
            public string BuildAuthorizationUrl(string redirectUri, string state) => $"https://example.invalid/authorize?redirect_uri={redirectUri}&state={state}";
            public Task<string?> ExchangeAuthorizationCodeAsync(string code, string redirectUri) => Task.FromResult<string?>(null);
        }
    }
}
