using BiatecOIDC.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class SpendingLimitServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Fake";
        private const string TestAddress = "ADDR1";

        private FakeCloudStorageProvider _fakeProvider = null!;
        private Mock<ICloudStorageProviderCatalog> _mockCatalog = null!;
        private Mock<IExchangeRateService> _mockExchangeRateService = null!;
        private SpendingLimitService _service = null!;
        private AesOptions _aesOptionsValue = null!;

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

        [SetUp]
        public void SetUp()
        {
            _fakeProvider = new FakeCloudStorageProvider();
            _mockCatalog = new Mock<ICloudStorageProviderCatalog>();
            _mockCatalog.Setup(c => c.Resolve(It.IsAny<string>())).Returns(_fakeProvider);

            _mockExchangeRateService = new Mock<IExchangeRateService>();
            _mockExchangeRateService
                .Setup(e => e.IsSupportedCurrencyAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            // 1:1 passthrough by default - individual tests override this to exercise currency conversion.
            _mockExchangeRateService
                .Setup(e => e.ConvertFromUsdAsync(It.IsAny<decimal>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((decimal amount, string _, CancellationToken _) => amount);

            var aesOptions = new Mock<IOptionsMonitor<AesOptions>>();
            _aesOptionsValue = BuildAesOptions("gen-1", MakeKeyEntry("gen-1", 1));
            aesOptions.Setup(a => a.CurrentValue).Returns(() => _aesOptionsValue);

            var mockEnvironment = new Mock<IHostEnvironment>();
            mockEnvironment.Setup(e => e.EnvironmentName).Returns(Environments.Development);

            _service = new SpendingLimitService(_mockCatalog.Object, _mockExchangeRateService.Object, aesOptions.Object, mockEnvironment.Object, new Mock<ILogger<SpendingLimitService>>().Object);
        }

        [Test]
        public async Task GetLimitsAsync_NeverConfigured_ReturnsUsdUnboundedDefault()
        {
            var result = await _service.GetLimitsAsync(TestEmail, TestProvider, "token");

            Assert.That(result.CurrencyCode, Is.EqualTo("USD"));
            Assert.That(result.DailyLimit, Is.EqualTo(0));
            Assert.That(result.WeeklyLimit, Is.EqualTo(0));
            Assert.That(result.MonthlyLimit, Is.EqualTo(0));
        }

        [Test]
        public async Task SetThenGet_RoundTripsSettings_CurrencyNormalizedUppercase()
        {
            var settings = new SpendingLimitSettings { CurrencyCode = "eur", DailyLimit = 100, WeeklyLimit = 500, MonthlyLimit = 2000 };

            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", settings);
            var result = await _service.GetLimitsAsync(TestEmail, TestProvider, "token");

            Assert.That(result.CurrencyCode, Is.EqualTo("EUR"));
            Assert.That(result.DailyLimit, Is.EqualTo(100));
            Assert.That(result.WeeklyLimit, Is.EqualTo(500));
            Assert.That(result.MonthlyLimit, Is.EqualTo(2000));
        }

        [Test]
        public async Task GetLimitsAsync_SettingsFileUnderHistoricalKey_MigratesToActiveKeyAndDeletesOldFile()
        {
            // Simulate a key rotation: the limits file was encrypted+named under a now-historical
            // generation before this test's active key ("gen-1") became active.
            var historicalEntry = MakeKeyEntry("gen-0", 9);
            _aesOptionsValue = BuildAesOptions("gen-1", MakeKeyEntry("gen-1", 1), historicalEntry);

            var settings = new SpendingLimitSettings { CurrencyCode = "EUR", DailyLimit = 50, WeeklyLimit = 0, MonthlyLimit = 0 };
            var plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(settings, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var historicalKeyBytes = BiatecSelfCustodyCore.Helper.AesKeyRingResolver.KeyBytes(historicalEntry);
            var historicalIvBytes = BiatecSelfCustodyCore.Helper.AesKeyRingResolver.IvBytes(historicalEntry);
            var encrypted = BiatecSelfCustodyCore.Helper.AesEncryptionHelper.Encrypt(plaintext, historicalKeyBytes, historicalIvBytes, TestEmail);
            var historicalFileName = "SpendingLimits." + BiatecSelfCustodyCore.Helper.AesEncryptionHelper.MakeAesId(historicalKeyBytes, historicalIvBytes) + ".dat";
            _fakeProvider.Files[historicalFileName] = encrypted;

            var result = await _service.GetLimitsAsync(TestEmail, TestProvider, "token");

            Assert.That(result.CurrencyCode, Is.EqualTo("EUR"));
            Assert.That(result.DailyLimit, Is.EqualTo(50));
            Assert.That(_fakeProvider.Files.ContainsKey(historicalFileName), Is.False, "stale file under the retired key should have been deleted");

            var activeKey = _aesOptionsValue.Keys.First(k => k.KeyId == "gen-1");
            var activeFileName = "SpendingLimits." + BiatecSelfCustodyCore.Helper.AesEncryptionHelper.MakeAesId(
                BiatecSelfCustodyCore.Helper.AesKeyRingResolver.KeyBytes(activeKey),
                BiatecSelfCustodyCore.Helper.AesKeyRingResolver.IvBytes(activeKey)) + ".dat";
            Assert.That(_fakeProvider.Files.ContainsKey(activeFileName), Is.True, "settings should now live under the active key's file");
        }

        [Test]
        public void SetLimitsAsync_UnsupportedCurrency_ThrowsAndDoesNotPersist()
        {
            _mockExchangeRateService.Setup(e => e.IsSupportedCurrencyAsync("XYZ", It.IsAny<CancellationToken>())).ReturnsAsync(false);

            Assert.ThrowsAsync<UnsupportedCurrencyException>(async () =>
                await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { CurrencyCode = "XYZ" }));
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_FullyUnbounded_NeverConsultsLedgerOrThrows()
        {
            await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 1_000_000m, TestAddress, 0);
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_UnderDailyLimit_DoesNotThrow()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 100 });

            await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 50m, TestAddress, 0);
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_ExceedsDailyLimit_ThrowsWithWindowDetails()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { CurrencyCode = "USD", DailyLimit = 100 });

            var ex = Assert.ThrowsAsync<SpendingLimitExceededException>(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 150m, TestAddress, 0));

            Assert.That(ex!.Window, Is.EqualTo("global-daily"));
            Assert.That(ex.ProjectedAmount, Is.EqualTo(150m));
            Assert.That(ex.Limit, Is.EqualTo(100m));
            Assert.That(ex.CurrencyCode, Is.EqualTo("USD"));
        }

        [Test]
        public async Task RecordSpendAsync_ThenEnsureWithinLimits_AccumulatesAcrossCalls()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 100 });
            await _service.RecordSpendAsync(TestEmail, TestProvider, "token", new[]
            {
                new SpendingLedgerEntry { TimestampUtc = DateTimeOffset.UtcNow, AmountUsd = 80m, AssetId = 0, Kind = "Payment", PrimaryAddress = TestAddress, Slot = 0 }
            });

            // 80 already spent + 30 more would be 110, over the 100 limit.
            Assert.ThrowsAsync<SpendingLimitExceededException>(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 30m, TestAddress, 0));

            // But 80 + 15 = 95 stays under it.
            Assert.DoesNotThrowAsync(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 15m, TestAddress, 0));
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_OnlyWeeklyConfigured_DailySpendDoesNotBlockIt()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { WeeklyLimit = 1000 });
            await _service.RecordSpendAsync(TestEmail, TestProvider, "token", new[]
            {
                new SpendingLedgerEntry { TimestampUtc = DateTimeOffset.UtcNow, AmountUsd = 5000m, AssetId = 0, Kind = "Payment", PrimaryAddress = TestAddress, Slot = 0 }
            });

            // Daily has no limit configured, so a huge existing spend today doesn't block a new one...
            // but it's within the same week, so the weekly limit (which IS configured) still applies.
            var ex = Assert.ThrowsAsync<SpendingLimitExceededException>(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 1m, TestAddress, 0));
            Assert.That(ex!.Window, Is.EqualTo("global-weekly"));
        }

        [Test]
        public async Task RecordSpendAsync_PrunesEntriesOlderThanThirtyDays()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { MonthlyLimit = 100 });

            // This entry is already outside every window (including monthly, the longest) the moment it's recorded.
            await _service.RecordSpendAsync(TestEmail, TestProvider, "token", new[]
            {
                new SpendingLedgerEntry { TimestampUtc = DateTimeOffset.UtcNow.AddDays(-45), AmountUsd = 999_999m, AssetId = 0, Kind = "Payment", PrimaryAddress = TestAddress, Slot = 0 }
            });

            // If the stale entry weren't pruned, even a tiny new spend would appear to exceed the monthly limit.
            Assert.DoesNotThrowAsync(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 50m, TestAddress, 0));
        }

        // ───────────────────────── Per-address + global limits ─────────────────────────

        [Test]
        public async Task GetLimitsAsync_PerAddress_NeverConfigured_ReturnsUnboundedDefault()
        {
            var result = await _service.GetLimitsAsync(TestEmail, TestProvider, "token", TestAddress, 2);

            Assert.That(result.DailyLimit, Is.EqualTo(0));
            Assert.That(result.WeeklyLimit, Is.EqualTo(0));
            Assert.That(result.MonthlyLimit, Is.EqualTo(0));
        }

        [Test]
        public async Task SetThenGet_PerAddress_RoundTripsAndDoesNotAffectGlobal()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { CurrencyCode = "EUR", DailyLimit = 25 }, TestAddress, 2);

            var addressResult = await _service.GetLimitsAsync(TestEmail, TestProvider, "token", TestAddress, 2);
            var globalResult = await _service.GetLimitsAsync(TestEmail, TestProvider, "token");
            var otherSlotResult = await _service.GetLimitsAsync(TestEmail, TestProvider, "token", TestAddress, 3);

            Assert.That(addressResult.CurrencyCode, Is.EqualTo("EUR"));
            Assert.That(addressResult.DailyLimit, Is.EqualTo(25));
            Assert.That(globalResult.DailyLimit, Is.EqualTo(0), "setting a per-address limit must not touch the global bucket");
            Assert.That(otherSlotResult.DailyLimit, Is.EqualTo(0), "a different slot on the same seed is a distinct bucket");
        }

        [Test]
        public async Task SetGlobal_DoesNotAffectExistingPerAddressLimits()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 25 }, TestAddress, 0);

            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 500 });

            var addressResult = await _service.GetLimitsAsync(TestEmail, TestProvider, "token", TestAddress, 0);
            Assert.That(addressResult.DailyLimit, Is.EqualTo(25));
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_GlobalLimitExceeded_ThrowsEvenWhenAddressUnbounded()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 100 });

            var ex = Assert.ThrowsAsync<SpendingLimitExceededException>(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 150m, TestAddress, 0));
            Assert.That(ex!.Window, Is.EqualTo("global-daily"));
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_AddressLimitExceeded_ThrowsEvenWhenGlobalUnbounded()
        {
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 100 }, TestAddress, 0);

            var ex = Assert.ThrowsAsync<SpendingLimitExceededException>(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 150m, TestAddress, 0));
            Assert.That(ex!.Window, Is.EqualTo("address-daily"));
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_SpendOnDifferentAddress_DoesNotCountTowardThisAddressLimit()
        {
            const string otherAddress = "ADDR2";
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 100 }, TestAddress, 0);
            await _service.RecordSpendAsync(TestEmail, TestProvider, "token", new[]
            {
                new SpendingLedgerEntry { TimestampUtc = DateTimeOffset.UtcNow, AmountUsd = 90m, AssetId = 0, Kind = "Payment", PrimaryAddress = otherAddress, Slot = 0 }
            });

            // The 90 was spent by a different address - TestAddress's own 100 limit is untouched by it.
            Assert.DoesNotThrowAsync(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 50m, TestAddress, 0));
        }

        [Test]
        public async Task EnsureWithinLimitsAsync_SpendOnDifferentAddress_StillCountsTowardGlobalLimit()
        {
            const string otherAddress = "ADDR2";
            await _service.SetLimitsAsync(TestEmail, TestProvider, "token", new SpendingLimitSettings { DailyLimit = 100 });
            await _service.RecordSpendAsync(TestEmail, TestProvider, "token", new[]
            {
                new SpendingLedgerEntry { TimestampUtc = DateTimeOffset.UtcNow, AmountUsd = 90m, AssetId = 0, Kind = "Payment", PrimaryAddress = otherAddress, Slot = 0 }
            });

            // The global limit counts every address's spend together - 90 (other address) + 30 (this one) exceeds 100.
            var ex = Assert.ThrowsAsync<SpendingLimitExceededException>(async () =>
                await _service.EnsureWithinLimitsAsync(TestEmail, TestProvider, "token", 30m, TestAddress, 0));
            Assert.That(ex!.Window, Is.EqualTo("global-daily"));
        }

        [Test]
        public async Task GetLimitsAsync_LegacyFlatSettingsFile_MigratesToGlobalBucketOnRead()
        {
            var activeKey = _aesOptionsValue.Keys.First(k => k.KeyId == "gen-1");
            var legacySettings = new SpendingLimitSettings { CurrencyCode = "EUR", DailyLimit = 42 };
            var plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(legacySettings, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var encrypted = BiatecSelfCustodyCore.Helper.AesEncryptionHelper.Encrypt(
                plaintext,
                BiatecSelfCustodyCore.Helper.AesKeyRingResolver.KeyBytes(activeKey),
                BiatecSelfCustodyCore.Helper.AesKeyRingResolver.IvBytes(activeKey),
                TestEmail);
            var activeFileName = "SpendingLimits." + BiatecSelfCustodyCore.Helper.AesEncryptionHelper.MakeAesId(
                BiatecSelfCustodyCore.Helper.AesKeyRingResolver.KeyBytes(activeKey),
                BiatecSelfCustodyCore.Helper.AesKeyRingResolver.IvBytes(activeKey));
            _fakeProvider.Files[activeFileName + ".dat"] = encrypted;

            var result = await _service.GetLimitsAsync(TestEmail, TestProvider, "token");

            Assert.That(result.CurrencyCode, Is.EqualTo("EUR"));
            Assert.That(result.DailyLimit, Is.EqualTo(42));

            // Per-address limits are untouched/empty after migrating a legacy (global-only) file.
            var addressResult = await _service.GetLimitsAsync(TestEmail, TestProvider, "token", TestAddress, 0);
            Assert.That(addressResult.DailyLimit, Is.EqualTo(0));
        }

        [Test]
        public void GetLimitsAsync_EmptyEmail_Throws()
        {
            Assert.ThrowsAsync<ArgumentException>(async () => await _service.GetLimitsAsync(string.Empty, TestProvider, "token"));
        }

        [Test]
        public void SetLimitsAsync_EmptyEmail_Throws()
        {
            Assert.ThrowsAsync<ArgumentException>(async () =>
                await _service.SetLimitsAsync(string.Empty, TestProvider, "token", new SpendingLimitSettings()));
        }

        [Test]
        public void NoAccessTokenAndNoAmbientToken_ThrowsUnauthorized()
        {
            var providerWithNoAmbientToken = new FakeCloudStorageProvider(ambientAccessToken: null);
            _mockCatalog.Setup(c => c.Resolve(It.IsAny<string>())).Returns(providerWithNoAmbientToken);

            Assert.ThrowsAsync<UnauthorizedAccessException>(async () =>
                await _service.GetLimitsAsync(TestEmail, TestProvider, accessToken: null));
        }
    }
}
