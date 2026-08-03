using System.Security.Claims;
using Algorand;
using Algorand.Algod.Model;
using Algorand.Algod.Model.Transactions;
using Algorand.Utils;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using BiatecSelfCustodyCore.Repository;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class WalletControllerTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestNetwork = "algorand";

        private static readonly Account TestAccount = new();
        private static readonly string TestAddress = TestAccount.Address.EncodeAsString();
        private static readonly Digest TestGenesisHash = new(new byte[32]);

        private Mock<IJwtIssuerService> _mockJwtIssuerService = null!;
        private Mock<IWalletService> _mockWalletService = null!;
        private Mock<ISpendingLimitService> _mockSpendingLimitService = null!;
        private Mock<IExchangeRateService> _mockExchangeRateService = null!;
        private Mock<IProviderAccessTokenProtector> _mockProviderTokenProtector = null!;
        private Mock<ICloudStorageProviderCatalog> _mockProviderCatalog = null!;
        private Mock<ICloudStorageProvider> _mockCloudStorageProvider = null!;
        private Mock<ICloudAccountRepository> _mockAccountRepository = null!;
        private Mock<IAddressActivationService> _mockAddressActivationService = null!;
        private Mock<INetworkResolver> _mockNetworkResolver = null!;
        private WalletController _controller = null!;

        [SetUp]
        public void SetUp()
        {
            _mockJwtIssuerService = new Mock<IJwtIssuerService>();
            _mockWalletService = new Mock<IWalletService>();
            _mockSpendingLimitService = new Mock<ISpendingLimitService>();
            _mockExchangeRateService = new Mock<IExchangeRateService>();
            _mockProviderTokenProtector = new Mock<IProviderAccessTokenProtector>();

            // Default: no provider-token renewal available - existing tests that don't exercise the
            // renew-and-retry path keep observing the previous "no cached refresh token" behavior.
            _mockCloudStorageProvider = new Mock<ICloudStorageProvider>();
            _mockCloudStorageProvider
                .Setup(p => p.RefreshAccessTokenAsync(It.IsAny<string>()))
                .ReturnsAsync((ProviderTokenRefreshResult?)null);
            _mockProviderCatalog = new Mock<ICloudStorageProviderCatalog>();
            _mockProviderCatalog.Setup(c => c.Resolve(It.IsAny<string?>())).Returns(_mockCloudStorageProvider.Object);

            _mockAccountRepository = new Mock<ICloudAccountRepository>();
            // Default: TestAddress is a seed's own (native) primary address, resolvable with no
            // activation-registry entry at all - matches most tests' "just works" expectation.
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary> { new(TestAddress, DateTimeOffset.UtcNow, true) });

            _mockAddressActivationService = new Mock<IAddressActivationService>();
            _mockAddressActivationService
                .Setup(s => s.TryResolveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((AddressActivationEntry?)null);

            _mockNetworkResolver = new Mock<INetworkResolver>();
            _mockNetworkResolver
                .Setup(r => r.ResolveAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork
                {
                    Family = ChainFamily.Avm,
                    DisplayName = "Algorand Mainnet",
                    AvmChain = new AlgorandChain { GenesisId = "mainnet-v1.0", Name = "Algorand Mainnet", AlgodApiAddress = "https://algod.example.com", AlgodApiToken = "" }
                });

            _controller = new WalletController(
                _mockJwtIssuerService.Object,
                _mockWalletService.Object,
                _mockSpendingLimitService.Object,
                _mockExchangeRateService.Object,
                _mockProviderTokenProtector.Object,
                _mockProviderCatalog.Object,
                _mockAccountRepository.Object,
                _mockAddressActivationService.Object,
                _mockNetworkResolver.Object,
                new Mock<ILogger<WalletController>>().Object)
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
        }

        private void SetBearerHeader(string? token)
        {
            if (token != null)
            {
                _controller.ControllerContext.HttpContext.Request.Headers.Authorization = $"Bearer {token}";
            }
        }

        private void SetupValidToken(string token, params Claim[] extraClaims)
        {
            var claims = new List<Claim> { new(ClaimTypes.Email, TestEmail) };
            claims.AddRange(extraClaims);
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

            _mockJwtIssuerService
                .Setup(s => s.ValidateBearerAccessToken(token))
                .Returns((true, principal, (IDictionary<string, object>?)null, (string?)null));
        }

        private Task<IActionResult> Sign(SignTransactionGroupRequest request, string network = TestNetwork, string? address = null) =>
            _controller.SignTransactionGroup(network, address ?? TestAddress, request);

        private static string BuildPaymentTransactionBase64(Address? sender = null, Address? rekeyTo = null)
        {
            var pay = new PaymentTransaction
            {
                Sender = sender ?? TestAccount.Address,
                Receiver = sender ?? TestAccount.Address,
                Amount = 0,
                Fee = 1000,
                FirstValid = 1,
                LastValid = 1000,
                GenesisId = "testnet-v1.0",
                GenesisHash = TestGenesisHash,
                RekeyTo = rekeyTo
            };
            return Convert.ToBase64String(Encoder.EncodeToMsgPackOrdered(pay));
        }

        private static string BuildRekeyPaymentTransactionBase64() => BuildPaymentTransactionBase64(rekeyTo: new Account().Address);

        // ───────────────────────── Authentication/authorization gating ─────────────────────────

        [Test]
        public async Task SignTransactionGroup_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_InvalidToken_ReturnsUnauthorized()
        {
            SetBearerHeader("bad-token");
            _mockJwtIssuerService
                .Setup(s => s.ValidateBearerAccessToken("bad-token"))
                .Returns((false, null, null, "invalid_token"));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_TokenMissingSignClaim_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token"); // no "sign" claim

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroup_UnknownNetwork_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockNetworkResolver.Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } }, network: "notanetwork");

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_EvmNetwork_ReturnsNotImplemented()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockNetworkResolver.Setup(r => r.ResolveAsync("ethereum", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = "Ethereum" });

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } }, network: "ethereum");

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(501));
        }

        [Test]
        public async Task SignTransactionGroup_TokenHasSignClaim_CallsWalletServiceAndReturnsOk()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"), new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("provider-token");
            var signedBytes = new byte[] { 1, 2, 3 };
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "provider-token", TestAddress, 0))
                .ReturnsAsync(new List<byte[]> { signedBytes });

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64() }
            });

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SignTransactionGroupResponse;
            Assert.That(response!.SignedTransactions, Is.EqualTo(new List<string> { Convert.ToBase64String(signedBytes) }));
        }

        [Test]
        public async Task SignTransactionGroup_TransactionSenderDoesNotMatchRouteAddress_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            var otherAddress = new Account().Address;

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64(sender: otherAddress) }
            });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            var problem = objectResult.Value as ProblemDetails;
            Assert.That(problem!.Title, Is.EqualTo("sender_mismatch"));
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroup_UndecodableTransaction_SkipsSenderCheckAndLetsWalletServiceReportIt()
        {
            // A placeholder/garbage payload can't be sender-checked here - WalletService (mocked away in
            // these tests) is what actually reports a real decode failure.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
        }

        // ───────────────────────── Cached provider access token resolution ─────────────────────────
        // No wallet endpoint accepts a caller-supplied access token - it's always resolved from the
        // bearer token's own encrypted provider_token claim (see WalletController's remarks).

        [Test]
        public async Task SignTransactionGroup_UsesCachedProviderTokenClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"), new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("decrypted-google-token");
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "decrypted-google-token", TestAddress, 0))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64() }
            });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "decrypted-google-token", TestAddress, 0), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroup_RekeyTransactionWithoutRekeyClaim_ReturnsForbiddenAndDoesNotSign()
        {
            SetBearerHeader("valid-token");
            // Has "sign" but not "rekey" - a normal wallet-scoped token, exactly what an attacker would
            // most plausibly get their hands on, which is the whole point of gating rekey separately.
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"));

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildRekeyPaymentTransactionBase64() }
            });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroup_RekeyTransactionWithRekeyClaim_Signs()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim("rekey", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"));
            var signedBytes = new byte[] { 7, 7, 7 };
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), TestAddress, 0))
                .ReturnsAsync(new List<byte[]> { signedBytes });

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildRekeyPaymentTransactionBase64() }
            });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), TestAddress, 0), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroup_NonRekeyTransactionWithoutRekeyClaim_StillSignsNormally()
        {
            // The rekey gate must not affect ordinary transactions at all.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), TestAddress, 0))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64() }
            });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_StaleProviderToken_RefreshesFromCachedRefreshClaimAndRetriesOnce()
        {
            // Reproduces the reported bug: the cached provider_token claim has gone stale (e.g. the Biatec
            // token was renewed after the underlying Google access token itself expired). With a cached
            // provider_refresh_token claim available, the call should transparently renew and succeed
            // instead of surfacing the storage provider's 401 to the caller.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token",
                new Claim("sign", "true"),
                new Claim(AuthSchemeNames.IdpClaimType, "Google"),
                new Claim(ProviderAccessTokenProtector.ClaimType, "protected-access-blob"),
                new Claim(ProviderAccessTokenProtector.RefreshClaimType, "protected-refresh-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-access-blob", TestEmail)).Returns("stale-google-token");
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-refresh-blob", TestEmail)).Returns("google-refresh-token");
            _mockCloudStorageProvider
                .Setup(p => p.RefreshAccessTokenAsync("google-refresh-token"))
                .ReturnsAsync(new ProviderTokenRefreshResult("renewed-google-token", null));

            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "stale-google-token", TestAddress, 0))
                .ThrowsAsync(new UnauthorizedAccessException("Google Drive access denied. The access token may be expired or invalid."));
            var signedBytes = new byte[] { 7 };
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "renewed-google-token", TestAddress, 0))
                .ReturnsAsync(new List<byte[]> { signedBytes });

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64() }
            });

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SignTransactionGroupResponse;
            Assert.That(response!.SignedTransactions, Is.EqualTo(new List<string> { Convert.ToBase64String(signedBytes) }));
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "renewed-google-token", TestAddress, 0), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroup_StaleProviderTokenAndNoCachedRefreshClaim_Returns401Unchanged()
        {
            // No provider_refresh_token claim cached (e.g. a session predating this feature) - the
            // original 401 must surface unchanged rather than retrying with nothing to refresh from.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token",
                new Claim("sign", "true"),
                new Claim(AuthSchemeNames.IdpClaimType, "Google"),
                new Claim(ProviderAccessTokenProtector.ClaimType, "protected-access-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-access-blob", TestEmail)).Returns("stale-google-token");
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "stale-google-token", TestAddress, 0))
                .ThrowsAsync(new UnauthorizedAccessException("Google Drive access denied. The access token may be expired or invalid."));

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64() }
            });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
            _mockCloudStorageProvider.Verify(p => p.RefreshAccessTokenAsync(It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task GetSpendingLimit_UsesCachedProviderTokenClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("decrypted-google-token");
            _mockSpendingLimitService
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), "decrypted-google-token", null, 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpendingLimitSettings());

            var result = await _controller.GetSpendingLimit();

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockSpendingLimitService.Verify(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), "decrypted-google-token", null, 0, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateSpendingLimit_UsesCachedProviderTokenClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"), new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("decrypted-google-token");

            await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { DailyLimit = 1 });

            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(TestEmail, It.IsAny<string>(), "decrypted-google-token", It.IsAny<SpendingLimitSettings>(), null, 0, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroup_EmptyTransactionsList_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string>() });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_NonBase64Transaction_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "not-base64!!" } });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_SpendingLimitExceeded_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ThrowsAsync(new SpendingLimitExceededException("daily", 500m, 100m, "USD"));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        }

        [Test]
        public async Task SignTransactionGroup_WalletServiceThrowsFormatException_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ThrowsAsync(new FormatException("bad tx"));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_WalletServiceThrowsUnauthorizedAccess_Returns401()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ThrowsAsync(new UnauthorizedAccessException("expired"));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        }

        [Test]
        public async Task SignTransactionGroup_AssetValuationFails_Returns503()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()))
                .ThrowsAsync(new AssetValuationException(0, new InvalidOperationException("no route")));

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        }

        [Test]
        public async Task SignTransactionGroup_UnknownAddress_ReturnsBadRequestAddressNotActive()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            // No seed matches, and the activation registry has nothing for it (default mock behavior).
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>());

            var result = await Sign(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } }, address: "NOTKNOWNADDR");

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status400BadRequest));
            var problem = objectResult.Value as ProblemDetails;
            Assert.That(problem!.Title, Is.EqualTo("address_not_active"));
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<int>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroup_ActivatedExternalAddress_ForwardsResolvedSeedAndSlot()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            var externalAddress = new Account().Address;
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>()); // no native match
            _mockAddressActivationService
                .Setup(s => s.TryResolveAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), externalAddress.EncodeAsString(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddressActivationEntry { Address = externalAddress.EncodeAsString(), Family = "Avm", SeedAddress = "SEED-ADDR", Slot = 5 });
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), "SEED-ADDR", 5))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await Sign(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { BuildPaymentTransactionBase64(sender: externalAddress) }
            }, address: externalAddress.EncodeAsString());

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), "SEED-ADDR", 5), Times.Once);
        }

        // ───────────────────────── Spending limit endpoints ─────────────────────────

        [Test]
        public async Task GetSpendingLimit_NoManageLimitsClaim_StillReturnsCurrentLimits()
        {
            // GET is read-only identity verification only - any authenticated (openid) caller may read
            // their own limits, no manage-limits claim required.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token"); // no manage-limits claim
            _mockSpendingLimitService
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), null, 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpendingLimitSettings { CurrencyCode = "EUR", DailyLimit = 42m });

            var result = await _controller.GetSpendingLimit();

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SpendingLimitResponse;
            Assert.That(response!.CurrencyCode, Is.EqualTo("EUR"));
            Assert.That(response.DailyLimit, Is.EqualTo(42m));
            Assert.That(response.Address, Is.Null);
        }

        [Test]
        public async Task GetSpendingLimit_StorageAccessDenied_Returns401()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockSpendingLimitService
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), null, 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UnauthorizedAccessException("expired"));

            var result = await _controller.GetSpendingLimit();

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status401Unauthorized));
        }

        [Test]
        public async Task UpdateSpendingLimit_MissingClaim_ReturnsForbiddenAndDoesNotUpdate()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { DailyLimit = 100 });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SpendingLimitSettings>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task UpdateSpendingLimit_HasClaim_UpdatesAndReturnsOk()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"));

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { CurrencyCode = "CZK", DailyLimit = 1000, WeeklyLimit = 5000, MonthlyLimit = 20000 });

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SpendingLimitResponse;
            Assert.That(response!.CurrencyCode, Is.EqualTo("CZK"));
            Assert.That(response.DailyLimit, Is.EqualTo(1000));
            Assert.That(response.WeeklyLimit, Is.EqualTo(5000));
            Assert.That(response.MonthlyLimit, Is.EqualTo(20000));
            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(
                TestEmail, It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<SpendingLimitSettings>(x => x.CurrencyCode == "CZK" && x.DailyLimit == 1000 && x.WeeklyLimit == 5000 && x.MonthlyLimit == 20000),
                null, 0, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateSpendingLimit_BlankCurrency_DefaultsToUsd()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"));

            await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { CurrencyCode = "  ", DailyLimit = 1 });

            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(),
                It.Is<SpendingLimitSettings>(x => x.CurrencyCode == "USD"),
                null, 0, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateSpendingLimit_SignClaimAloneIsNotEnough_ReturnsForbidden()
        {
            // A token authorized only for signing must not also be able to change spending limits.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { DailyLimit = 1 });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        }

        [Test]
        public async Task UpdateSpendingLimit_UnsupportedCurrency_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"));
            _mockSpendingLimitService
                .Setup(s => s.SetLimitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SpendingLimitSettings>(), null, 0, It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UnsupportedCurrencyException("XYZ"));

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { CurrencyCode = "XYZ", DailyLimit = 1 });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        // ───────────────────────── Currency list endpoint ─────────────────────────

        [Test]
        public async Task GetSupportedCurrencies_ReturnsCurrenciesFromExchangeRateService()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockExchangeRateService
                .Setup(e => e.GetSupportedCurrenciesAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new List<CurrencyRate>
                {
                    new() { Code = "USD", DisplayName = "US Dollar", UsdPerUnit = 1m },
                    new() { Code = "EUR", DisplayName = "Euro", UsdPerUnit = 1.08m }
                });

            var result = await _controller.GetSupportedCurrencies();

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SupportedCurrenciesResponse;
            Assert.That(response!.Currencies, Has.Count.EqualTo(2));
            Assert.That(response.Currencies[1].Code, Is.EqualTo("EUR"));
            Assert.That(response.Currencies[1].UsdPerUnit, Is.EqualTo(1.08m));
        }

        [Test]
        public async Task GetSupportedCurrencies_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await _controller.GetSupportedCurrencies();

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        [Test]
        public async Task GetSupportedCurrencies_ServiceUnavailable_Returns503()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockExchangeRateService
                .Setup(e => e.GetSupportedCurrenciesAsync(It.IsAny<CancellationToken>()))
                .ThrowsAsync(new InvalidOperationException("CNB unreachable"));

            var result = await _controller.GetSupportedCurrencies();

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
        }

        // ───────────────────────── Per-address spending limits ─────────────────────────

        [Test]
        public async Task GetSpendingLimitForAddress_KnownAddress_ForwardsResolvedSeedAndSlot()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAddressActivationService
                .Setup(s => s.TryResolveAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "SOME-ADDR", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddressActivationEntry { Address = "SOME-ADDR", Family = "Avm", SeedAddress = "SEED-ADDR", Slot = 3 });
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>());
            _mockSpendingLimitService
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "SEED-ADDR", 3, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpendingLimitSettings { DailyLimit = 7 });

            var result = await _controller.GetSpendingLimitForAddress(TestNetwork, "SOME-ADDR");

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SpendingLimitResponse;
            Assert.That(response!.DailyLimit, Is.EqualTo(7));
            Assert.That(response.SeedAddress, Is.EqualTo("SEED-ADDR"));
            Assert.That(response.Slot, Is.EqualTo(3));
            Assert.That(response.Address, Is.EqualTo("SOME-ADDR"));
            Assert.That(response.Network, Is.EqualTo(TestNetwork));
        }

        [Test]
        public async Task GetSpendingLimitForAddress_UnknownAddress_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>());

            var result = await _controller.GetSpendingLimitForAddress(TestNetwork, "UNKNOWN");

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task GetSpendingLimitForAddress_UnknownNetwork_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockNetworkResolver.Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await _controller.GetSpendingLimitForAddress("notanetwork", TestAddress);

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task UpdateSpendingLimitForAddress_KnownAddress_ForwardsResolvedSeedAndSlot()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"));
            _mockAddressActivationService
                .Setup(s => s.TryResolveAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "SOME-ADDR", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddressActivationEntry { Address = "SOME-ADDR", Family = "Avm", SeedAddress = "SEED-ADDR", Slot = 4 });
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>());

            await _controller.UpdateSpendingLimitForAddress(TestNetwork, "SOME-ADDR", new UpdateSpendingLimitRequest { DailyLimit = 1 });

            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(
                TestEmail, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SpendingLimitSettings>(), "SEED-ADDR", 4, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateSpendingLimitForAddress_MissingClaim_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");

            var result = await _controller.UpdateSpendingLimitForAddress(TestNetwork, TestAddress, new UpdateSpendingLimitRequest { DailyLimit = 1 });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        }

        // ───────────────────────── Address derivation (multi-network) ─────────────────────────

        [Test]
        public async Task GetAddress_KnownSeed_ReturnsDerivedAvmAndEvmAddresses()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 2, It.IsAny<string?>()))
                .ReturnsAsync("DERIVED-ADDR");
            _mockAccountRepository
                .Setup(r => r.DeriveEvmAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 2, It.IsAny<string?>()))
                .ReturnsAsync("0xDERIVED");

            var result = await _controller.GetAddress("ADDR1", 2);

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as DerivedAddressResponse;
            Assert.That(response!.Address, Is.EqualTo("DERIVED-ADDR"));
            Assert.That(response.EvmAddress, Is.EqualTo("0xDERIVED"));
            Assert.That(response.SeedAddress, Is.EqualTo("ADDR1"));
            Assert.That(response.Slot, Is.EqualTo(2));
        }

        [Test]
        public async Task GetAddress_NonZeroSlot_ActivatesBothDerivedAddresses()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 2, It.IsAny<string?>()))
                .ReturnsAsync("DERIVED-ADDR");
            _mockAccountRepository
                .Setup(r => r.DeriveEvmAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 2, It.IsAny<string?>()))
                .ReturnsAsync("0xDERIVED");

            await _controller.GetAddress("ADDR1", 2);

            _mockAddressActivationService.Verify(s => s.ActivateAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "DERIVED-ADDR", "Avm", "ADDR1", 2, It.IsAny<CancellationToken>()), Times.Once);
            _mockAddressActivationService.Verify(s => s.ActivateAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "0xDERIVED", "Evm", "ADDR1", 2, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task GetAddress_NoSlotGiven_DefaultsToZeroAndDoesNotActivateAvmButActivatesEvm()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 0, It.IsAny<string?>()))
                .ReturnsAsync("DERIVED-ADDR");
            _mockAccountRepository
                .Setup(r => r.DeriveEvmAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 0, It.IsAny<string?>()))
                .ReturnsAsync("0xDERIVED");

            var result = await _controller.GetAddress("ADDR1", null);

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockAccountRepository.Verify(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "ADDR1", 0, It.IsAny<string?>()), Times.Once);
            // Slot 0 AVM is already the seed's own identifying address - no activation-registry entry needed.
            _mockAddressActivationService.Verify(s => s.ActivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), "DERIVED-ADDR", "Avm", It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
            // An EVM address is never a seed's own identifying address, even at slot 0 - always activated.
            _mockAddressActivationService.Verify(s => s.ActivateAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "0xDERIVED", "Evm", "ADDR1", 0, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task GetAddress_UnknownSeed_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "NOTAREALADDRESS", 0, It.IsAny<string?>()))
                .ThrowsAsync(new InvalidOperationException("No seed with address 'NOTAREALADDRESS' exists for this account."));

            var result = await _controller.GetAddress("NOTAREALADDRESS", null);

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task GetAddress_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await _controller.GetAddress("ADDR1", null);

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        // ───────────────────────── GET /wallet/{network}/{address}/info ─────────────────────────

        [Test]
        public async Task GetAddressInfo_NativeSeedPrimaryAddress_ReturnsActiveWithSlotZero()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");

            var result = await _controller.GetAddressInfo(TestNetwork, TestAddress);

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as AddressInfoResponse;
            Assert.That(response!.IsActive, Is.True);
            Assert.That(response.SeedAddress, Is.EqualTo(TestAddress));
            Assert.That(response.Slot, Is.EqualTo(0));
            Assert.That(response.Family, Is.EqualTo("Avm"));
        }

        [Test]
        public async Task GetAddressInfo_ActivatedAddress_ReturnsActiveWithStoredSeedAndSlot()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>());
            _mockAddressActivationService
                .Setup(s => s.TryResolveAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "SOME-ADDR", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AddressActivationEntry { Address = "SOME-ADDR", Family = "Avm", SeedAddress = "SEED-ADDR", Slot = 9 });

            var result = await _controller.GetAddressInfo(TestNetwork, "SOME-ADDR");

            var response = (result as OkObjectResult)!.Value as AddressInfoResponse;
            Assert.That(response!.IsActive, Is.True);
            Assert.That(response.SeedAddress, Is.EqualTo("SEED-ADDR"));
            Assert.That(response.Slot, Is.EqualTo(9));
        }

        [Test]
        public async Task GetAddressInfo_UnknownAddress_ReturnsNotActive()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockAccountRepository
                .Setup(r => r.ListSeedsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(new List<SeedSummary>());

            var result = await _controller.GetAddressInfo(TestNetwork, "NEVERSEEN");

            var response = (result as OkObjectResult)!.Value as AddressInfoResponse;
            Assert.That(response!.IsActive, Is.False);
            Assert.That(response.SeedAddress, Is.Null);
        }

        [Test]
        public async Task GetAddressInfo_UnknownNetwork_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockNetworkResolver.Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await _controller.GetAddressInfo("notanetwork", TestAddress);

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task GetAddressInfo_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await _controller.GetAddressInfo(TestNetwork, TestAddress);

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        // ───────────────────────── POST /wallet/{network}/{address}/activate ─────────────────────────
        // The on-chain rekey-verification path itself (an external AVM address actually being rekeyed to
        // the derived key) is exercised manually/E2E, same precedent as this repo's other live-network
        // calls (e.g. PublicAlgodDataSource) - only the branch logic reachable without a live algod call is
        // unit-tested here: the native (self-derived) trivial-success path, and the EVM-mismatch rejection.

        [Test]
        public async Task ActivateAddress_NativeDerivedAddress_ActivatesImmediatelyWithoutAnOnChainCheck()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockAccountRepository
                .Setup(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "SEED-ADDR", 3, It.IsAny<string?>()))
                .ReturnsAsync("DERIVED-ADDR");

            var result = await _controller.ActivateAddress(TestNetwork, "DERIVED-ADDR", new ActivateAddressRequest { SeedAddress = "SEED-ADDR", Slot = 3 });

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as AddressInfoResponse;
            Assert.That(response!.IsActive, Is.True);
            _mockAddressActivationService.Verify(s => s.ActivateAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), "DERIVED-ADDR", "Avm", "SEED-ADDR", 3, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task ActivateAddress_EvmAddressNotMatchingDerivedAddress_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockNetworkResolver.Setup(r => r.ResolveAsync("ethereum", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Family = ChainFamily.Evm, DisplayName = "Ethereum" });
            _mockAccountRepository
                .Setup(r => r.DeriveEvmAddressAsync(TestEmail, It.IsAny<string>(), "SEED-ADDR", 0, It.IsAny<string?>()))
                .ReturnsAsync("0xDERIVED");

            var result = await _controller.ActivateAddress("ethereum", "0xSOMEOTHERADDRESS", new ActivateAddressRequest { SeedAddress = "SEED-ADDR", Slot = 0 });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
            _mockAddressActivationService.Verify(s => s.ActivateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ActivateAddress_UnknownNetwork_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockNetworkResolver.Setup(r => r.ResolveAsync("notanetwork", It.IsAny<CancellationToken>())).ReturnsAsync((ResolvedNetwork?)null);

            var result = await _controller.ActivateAddress("notanetwork", TestAddress, new ActivateAddressRequest { SeedAddress = "SEED-ADDR", Slot = 0 });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task ActivateAddress_UnknownSeed_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockAccountRepository
                .Setup(r => r.DeriveAddressAsync(TestEmail, It.IsAny<string>(), "NOTAREALSEED", 0, It.IsAny<string?>()))
                .ThrowsAsync(new InvalidOperationException("No seed with address 'NOTAREALSEED' exists for this account."));

            var result = await _controller.ActivateAddress(TestNetwork, TestAddress, new ActivateAddressRequest { SeedAddress = "NOTAREALSEED", Slot = 0 });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task ActivateAddress_MissingSignClaim_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");

            var result = await _controller.ActivateAddress(TestNetwork, TestAddress, new ActivateAddressRequest { SeedAddress = "SEED-ADDR", Slot = 0 });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        }

        [Test]
        public async Task ActivateAddress_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await _controller.ActivateAddress(TestNetwork, TestAddress, new ActivateAddressRequest { SeedAddress = "SEED-ADDR", Slot = 0 });

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }
    }
}
