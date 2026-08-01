using System.Security.Claims;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;
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

        private Mock<IJwtIssuerService> _mockJwtIssuerService = null!;
        private Mock<IWalletService> _mockWalletService = null!;
        private Mock<ISpendingLimitService> _mockSpendingLimitService = null!;
        private Mock<IExchangeRateService> _mockExchangeRateService = null!;
        private Mock<IProviderAccessTokenProtector> _mockProviderTokenProtector = null!;
        private WalletController _controller = null!;

        [SetUp]
        public void SetUp()
        {
            _mockJwtIssuerService = new Mock<IJwtIssuerService>();
            _mockWalletService = new Mock<IWalletService>();
            _mockSpendingLimitService = new Mock<ISpendingLimitService>();
            _mockExchangeRateService = new Mock<IExchangeRateService>();
            _mockProviderTokenProtector = new Mock<IProviderAccessTokenProtector>();
            _controller = new WalletController(
                _mockJwtIssuerService.Object,
                _mockWalletService.Object,
                _mockSpendingLimitService.Object,
                _mockExchangeRateService.Object,
                _mockProviderTokenProtector.Object,
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

        // ───────────────────────── Authentication/authorization gating ─────────────────────────

        [Test]
        public async Task SignTransactionGroup_NoBearerToken_ReturnsUnauthorized()
        {
            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_InvalidToken_ReturnsUnauthorized()
        {
            SetBearerHeader("bad-token");
            _mockJwtIssuerService
                .Setup(s => s.ValidateBearerAccessToken("bad-token"))
                .Returns((false, null, null, "invalid_token"));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<UnauthorizedObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_TokenMissingSignClaim_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token"); // no "sign" claim

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroup_TokenHasSignClaim_CallsWalletServiceAndReturnsOk()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"));
            var signedBytes = new byte[] { 1, 2, 3 };
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "provider-token"))
                .ReturnsAsync(new List<byte[]> { signedBytes });

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { Convert.ToBase64String(new byte[] { 9, 9 }) },
                AccessToken = "provider-token"
            });

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SignTransactionGroupResponse;
            Assert.That(response!.SignedTransactions, Is.EqualTo(new List<string> { Convert.ToBase64String(signedBytes) }));
        }

        // ───────────────────────── Cached provider access token fallback ─────────────────────────

        [Test]
        public async Task SignTransactionGroup_NoExplicitAccessToken_FallsBackToCachedProviderTokenClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"), new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("decrypted-google-token");
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "decrypted-google-token"))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { Convert.ToBase64String(new byte[] { 9, 9 }) }
                // No AccessToken supplied.
            });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "decrypted-google-token"), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroup_ExplicitAccessTokenSupplied_TakesPrecedenceOverCachedClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"), new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "explicit-token"))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { Convert.ToBase64String(new byte[] { 9, 9 }) },
                AccessToken = "explicit-token"
            });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), "explicit-token"), Times.Once);
            // The cached claim must never even be decrypted when an explicit token was supplied.
            _mockProviderTokenProtector.Verify(p => p.Unprotect(It.IsAny<string?>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task SignTransactionGroup_NoExplicitTokenAndNoCachedClaim_PassesNullThrough()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"), new Claim(AuthSchemeNames.IdpClaimType, "Google"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), null))
                .ReturnsAsync(new List<byte[]> { new byte[] { 1 } });

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest
            {
                Transactions = new List<string> { Convert.ToBase64String(new byte[] { 9, 9 }) }
            });

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockWalletService.Verify(w => w.SignTransactionGroupAsync(TestEmail, "Google", It.IsAny<IReadOnlyList<byte[]>>(), null), Times.Once);
        }

        [Test]
        public async Task GetSpendingLimit_NoExplicitAccessToken_FallsBackToCachedProviderTokenClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("decrypted-google-token");
            _mockSpendingLimitService
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), "decrypted-google-token", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpendingLimitSettings());

            var result = await _controller.GetSpendingLimit(accessToken: null);

            Assert.That(result, Is.InstanceOf<OkObjectResult>());
            _mockSpendingLimitService.Verify(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), "decrypted-google-token", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task UpdateSpendingLimit_NoExplicitAccessToken_FallsBackToCachedProviderTokenClaim()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"), new Claim(ProviderAccessTokenProtector.ClaimType, "protected-blob"));
            _mockProviderTokenProtector.Setup(p => p.Unprotect("protected-blob", TestEmail)).Returns("decrypted-google-token");

            await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { DailyLimit = 1 });

            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(TestEmail, It.IsAny<string>(), "decrypted-google-token", It.IsAny<SpendingLimitSettings>(), It.IsAny<CancellationToken>()), Times.Once);
        }

        [Test]
        public async Task SignTransactionGroup_EmptyTransactionsList_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string>() });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_NonBase64Transaction_ReturnsBadRequest()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "not-base64!!" } });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_SpendingLimitExceeded_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>()))
                .ThrowsAsync(new SpendingLimitExceededException("daily", 500m, 100m, "USD"));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

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
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>()))
                .ThrowsAsync(new FormatException("bad tx"));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            Assert.That(result, Is.InstanceOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task SignTransactionGroup_WalletServiceThrowsUnauthorizedAccess_Returns401()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));
            _mockWalletService
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>()))
                .ThrowsAsync(new UnauthorizedAccessException("expired"));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

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
                .Setup(w => w.SignTransactionGroupAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>()))
                .ThrowsAsync(new AssetValuationException(0, new InvalidOperationException("no route")));

            var result = await _controller.SignTransactionGroup(new SignTransactionGroupRequest { Transactions = new List<string> { "AA==" } });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status503ServiceUnavailable));
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
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SpendingLimitSettings { CurrencyCode = "EUR", DailyLimit = 42m });

            var result = await _controller.GetSpendingLimit(accessToken: "provider-token");

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SpendingLimitResponse;
            Assert.That(response!.CurrencyCode, Is.EqualTo("EUR"));
            Assert.That(response.DailyLimit, Is.EqualTo(42m));
        }

        [Test]
        public async Task GetSpendingLimit_StorageAccessDenied_Returns401()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");
            _mockSpendingLimitService
                .Setup(s => s.GetLimitsAsync(TestEmail, It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new UnauthorizedAccessException("expired"));

            var result = await _controller.GetSpendingLimit(accessToken: null);

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
            _mockSpendingLimitService.Verify(s => s.SetLimitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SpendingLimitSettings>(), It.IsAny<CancellationToken>()), Times.Never);
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
                It.IsAny<CancellationToken>()), Times.Once);
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
                It.IsAny<CancellationToken>()), Times.Once);
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
                .Setup(s => s.SetLimitsAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<SpendingLimitSettings>(), It.IsAny<CancellationToken>()))
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
    }
}
