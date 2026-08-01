using BiatecOIDC.BusinessLogic;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.Extensions.Logging;
using Moq;
using StackExchange.Redis;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class VaultBackupServiceTests
    {
        private const string TestEmail = "user@example.com";

        private Mock<IConnectionMultiplexer> _mockRedis = null!;
        private Mock<IDatabase> _mockDatabase = null!;
        private Mock<ICloudStorageProviderCatalog> _mockCatalog = null!;
        private Mock<ICloudStorageProvider> _mockGoogleProvider = null!;
        private Mock<ICloudStorageProvider> _mockMicrosoftProvider = null!;
        private Mock<ICloudAccountRepository> _mockAccountRepository = null!;
        private VaultBackupService _service = null!;

        [SetUp]
        public void SetUp()
        {
            _mockRedis = new Mock<IConnectionMultiplexer>();
            _mockDatabase = new Mock<IDatabase>();
            _mockRedis.Setup(r => r.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);

            _mockGoogleProvider = new Mock<ICloudStorageProvider>();
            _mockGoogleProvider.Setup(p => p.Name).Returns("Google");
            _mockGoogleProvider.Setup(p => p.DisplayName).Returns("Google");
            _mockGoogleProvider.Setup(p => p.IsConfigured).Returns(true);

            _mockMicrosoftProvider = new Mock<ICloudStorageProvider>();
            _mockMicrosoftProvider.Setup(p => p.Name).Returns("Microsoft");
            _mockMicrosoftProvider.Setup(p => p.DisplayName).Returns("Microsoft");
            _mockMicrosoftProvider.Setup(p => p.IsConfigured).Returns(true);

            _mockCatalog = new Mock<ICloudStorageProviderCatalog>();
            _mockCatalog.Setup(c => c.All).Returns(new[] { _mockGoogleProvider.Object, _mockMicrosoftProvider.Object });
            _mockCatalog.Setup(c => c.Resolve("Google")).Returns(_mockGoogleProvider.Object);
            _mockCatalog.Setup(c => c.Resolve("Microsoft")).Returns(_mockMicrosoftProvider.Object);

            _mockAccountRepository = new Mock<ICloudAccountRepository>();

            _service = new VaultBackupService(_mockRedis.Object, _mockCatalog.Object, _mockAccountRepository.Object, new Mock<ILogger<VaultBackupService>>().Object);
        }

        // ───────────────────────── StartAsync ─────────────────────────

        [Test]
        public void StartAsync_TargetSameAsPrimary_Throws()
        {
            Assert.That(async () => await _service.StartAsync(TestEmail, "Google", "Google"),
                Throws.InvalidOperationException);
        }

        [Test]
        public void StartAsync_TargetSameAsPrimary_CaseInsensitive_Throws()
        {
            Assert.That(async () => await _service.StartAsync(TestEmail, "Google", "google"),
                Throws.InvalidOperationException);
        }

        [Test]
        public void StartAsync_UnknownProvider_Throws()
        {
            Assert.That(async () => await _service.StartAsync(TestEmail, "Google", "Dropbox"),
                Throws.InvalidOperationException);
        }

        [Test]
        public void StartAsync_UnconfiguredProvider_Throws()
        {
            _mockMicrosoftProvider.Setup(p => p.IsConfigured).Returns(false);

            Assert.That(async () => await _service.StartAsync(TestEmail, "Google", "Microsoft"),
                Throws.InvalidOperationException);
        }

        [Test]
        public async Task StartAsync_ValidTarget_StoresPendingRecordAndReturnsANonEmptyLinkId()
        {
            string? storedKey = null;
            string? storedValue = null;
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, value, _, _, _, _) => { storedKey = key!; storedValue = value!; })
                .ReturnsAsync(true);

            var linkId = await _service.StartAsync(TestEmail, "Google", "Microsoft");

            Assert.That(linkId, Is.Not.Null.And.Not.Empty);
            Assert.That(storedKey, Does.StartWith("vaultbackup:pending:"));
            Assert.That(storedValue, Does.Contain(TestEmail));
            Assert.That(storedValue, Does.Contain("Microsoft"));
        }

        // ───────────────────────── GetPendingAsync ─────────────────────────

        [Test]
        public async Task GetPendingAsync_UnknownLinkId_ReturnsNull()
        {
            _mockDatabase.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);

            var result = await _service.GetPendingAsync("missing-link");

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task GetPendingAsync_Existing_ReturnsEmailAndTargetProvider()
        {
            _mockDatabase
                .Setup(d => d.StringGetAsync("vaultbackup:pending:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"""{"email":"user@example.com","targetProvider":"Microsoft"}""");

            var result = await _service.GetPendingAsync("link-1");

            Assert.That(result, Is.Not.Null);
            Assert.That(result!.Email, Is.EqualTo(TestEmail));
            Assert.That(result.TargetProvider, Is.EqualTo("Microsoft"));
        }

        // ───────────────────────── HandleCallbackAsync ─────────────────────────

        [Test]
        public async Task HandleCallbackAsync_NoPendingRecord_ReturnsFailure()
        {
            _mockDatabase.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);

            var (success, error) = await _service.HandleCallbackAsync("missing-link", "code", "https://redirect");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task HandleCallbackAsync_CodeExchangeFails_ReturnsFailure()
        {
            _mockDatabase
                .Setup(d => d.StringGetAsync("vaultbackup:pending:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"""{"email":"user@example.com","targetProvider":"Microsoft"}""");
            _mockMicrosoftProvider.Setup(p => p.ExchangeAuthorizationCodeAsync("bad-code", "https://redirect")).ReturnsAsync((string?)null);

            var (success, error) = await _service.HandleCallbackAsync("link-1", "bad-code", "https://redirect");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task HandleCallbackAsync_NoWriteAccess_ReturnsFailure()
        {
            _mockDatabase
                .Setup(d => d.StringGetAsync("vaultbackup:pending:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"""{"email":"user@example.com","targetProvider":"Microsoft"}""");
            _mockMicrosoftProvider.Setup(p => p.ExchangeAuthorizationCodeAsync("code", "https://redirect")).ReturnsAsync("ms-token");
            _mockMicrosoftProvider.Setup(p => p.HasWriteAccessAsync("ms-token")).ReturnsAsync(false);

            var (success, error) = await _service.HandleCallbackAsync("link-1", "code", "https://redirect");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task HandleCallbackAsync_Success_StoresLinkedRecordAndDeletesPending()
        {
            _mockDatabase
                .Setup(d => d.StringGetAsync("vaultbackup:pending:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"""{"email":"user@example.com","targetProvider":"Microsoft"}""");
            _mockMicrosoftProvider.Setup(p => p.ExchangeAuthorizationCodeAsync("code", "https://redirect")).ReturnsAsync("ms-token");
            _mockMicrosoftProvider.Setup(p => p.HasWriteAccessAsync("ms-token")).ReturnsAsync(true);

            string? linkedKey = null;
            string? linkedValue = null;
            _mockDatabase
                .Setup(d => d.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<TimeSpan?>(), It.IsAny<bool>(), It.IsAny<When>(), It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, value, _, _, _, _) => { linkedKey = key!; linkedValue = value!; })
                .ReturnsAsync(true);

            var (success, error) = await _service.HandleCallbackAsync("link-1", "code", "https://redirect");

            Assert.That(success, Is.True);
            Assert.That(error, Is.Null);
            Assert.That(linkedKey, Is.EqualTo("vaultbackup:linked:link-1"));
            Assert.That(linkedValue, Does.Contain("ms-token"));
            _mockDatabase.Verify(d => d.KeyDeleteAsync("vaultbackup:pending:link-1", It.IsAny<CommandFlags>()), Times.Once);
        }

        // ───────────────────────── CompleteAsync ─────────────────────────

        [Test]
        public async Task CompleteAsync_NoLinkedRecord_ReturnsFailure()
        {
            _mockDatabase.Setup(d => d.StringGetDeleteAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>())).ReturnsAsync(RedisValue.Null);

            var (success, error) = await _service.CompleteAsync(TestEmail, "Google", "primary-token", "link-1");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task CompleteAsync_EmailMismatch_ReturnsFailure()
        {
            _mockDatabase
                .Setup(d => d.StringGetDeleteAsync("vaultbackup:linked:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)"""{"email":"someone-else@example.com","targetProvider":"Microsoft","targetProviderAccessToken":"ms-token"}""");

            var (success, error) = await _service.CompleteAsync(TestEmail, "Google", "primary-token", "link-1");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task CompleteAsync_NoPrimaryAccessToken_ReturnsFailure()
        {
            _mockDatabase
                .Setup(d => d.StringGetDeleteAsync("vaultbackup:linked:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)$$"""{"email":"{{TestEmail}}","targetProvider":"Microsoft","targetProviderAccessToken":"ms-token"}""");

            var (success, error) = await _service.CompleteAsync(TestEmail, "Google", null, "link-1");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public async Task CompleteAsync_Success_CopiesVaultBytesToTargetProvider()
        {
            _mockDatabase
                .Setup(d => d.StringGetDeleteAsync("vaultbackup:linked:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)$$"""{"email":"{{TestEmail}}","targetProvider":"Microsoft","targetProviderAccessToken":"ms-token"}""");
            var vaultBytes = new byte[] { 1, 2, 3, 4 };
            _mockAccountRepository
                .Setup(r => r.GetEncryptedVaultForBackupAsync(TestEmail, "Google", "primary-token"))
                .ReturnsAsync(("AVMAccount.abc123.dat", vaultBytes));

            var (success, error) = await _service.CompleteAsync(TestEmail, "Google", "primary-token", "link-1");

            Assert.That(success, Is.True);
            Assert.That(error, Is.Null);
            _mockMicrosoftProvider.Verify(p => p.UploadAsync("AVMAccount.abc123.dat", vaultBytes, "ms-token"), Times.Once);
        }

        [Test]
        public async Task CompleteAsync_CopyThrows_ReturnsFailureInsteadOfThrowing()
        {
            _mockDatabase
                .Setup(d => d.StringGetDeleteAsync("vaultbackup:linked:link-1", It.IsAny<CommandFlags>()))
                .ReturnsAsync((RedisValue)$$"""{"email":"{{TestEmail}}","targetProvider":"Microsoft","targetProviderAccessToken":"ms-token"}""");
            _mockAccountRepository
                .Setup(r => r.GetEncryptedVaultForBackupAsync(TestEmail, "Google", "primary-token"))
                .ThrowsAsync(new InvalidOperationException("boom"));

            var (success, error) = await _service.CompleteAsync(TestEmail, "Google", "primary-token", "link-1");

            Assert.That(success, Is.False);
            Assert.That(error, Is.Not.Null.And.Not.Empty);
        }
    }
}
