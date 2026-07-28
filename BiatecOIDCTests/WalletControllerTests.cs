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
        private Mock<ILogger<WalletController>> _mockLogger = null!;
        private WalletController _controller = null!;

        [SetUp]
        public void SetUp()
        {
            _mockJwtIssuerService = new Mock<IJwtIssuerService>();
            _mockWalletService = new Mock<IWalletService>();
            _mockSpendingLimitService = new Mock<ISpendingLimitService>();
            _mockLogger = new Mock<ILogger<WalletController>>();
            _controller = new WalletController(_mockJwtIssuerService.Object, _mockWalletService.Object, _mockSpendingLimitService.Object, _mockLogger.Object)
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
                .ThrowsAsync(new SpendingLimitExceededException(BiatecOIDC.Helper.AlgorandTransactionKind.Payment, 500, 100));

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

        // ───────────────────────── Spending limit endpoints ─────────────────────────

        [Test]
        public async Task GetSpendingLimit_MissingManageLimitsClaim_ReturnsForbidden()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token"); // no manage-limits claim

            var result = await _controller.GetSpendingLimit();

            var objectResult = result as ObjectResult;
            Assert.That(objectResult, Is.Not.Null);
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        }

        [Test]
        public async Task GetSpendingLimit_HasClaim_ReturnsCurrentLimit()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"));
            _mockSpendingLimitService.Setup(s => s.GetMaxAmountPerTransactionAsync(TestEmail)).ReturnsAsync(42UL);

            var result = await _controller.GetSpendingLimit();

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SpendingLimitResponse;
            Assert.That(response!.MaxAmountPerTransaction, Is.EqualTo(42UL));
        }

        [Test]
        public async Task UpdateSpendingLimit_MissingClaim_ReturnsForbiddenAndDoesNotUpdate()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token");

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { MaxAmountPerTransaction = 100 });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
            _mockSpendingLimitService.Verify(s => s.SetMaxAmountPerTransactionAsync(It.IsAny<string>(), It.IsAny<ulong>()), Times.Never);
        }

        [Test]
        public async Task UpdateSpendingLimit_HasClaim_UpdatesAndReturnsOk()
        {
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("manage-limits", "true"));

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { MaxAmountPerTransaction = 777 });

            var okResult = result as OkObjectResult;
            Assert.That(okResult, Is.Not.Null);
            var response = okResult!.Value as SpendingLimitResponse;
            Assert.That(response!.MaxAmountPerTransaction, Is.EqualTo(777UL));
            _mockSpendingLimitService.Verify(s => s.SetMaxAmountPerTransactionAsync(TestEmail, 777UL), Times.Once);
        }

        [Test]
        public async Task UpdateSpendingLimit_SignClaimAloneIsNotEnough_ReturnsForbidden()
        {
            // A token authorized only for signing must not also be able to change spending limits.
            SetBearerHeader("valid-token");
            SetupValidToken("valid-token", new Claim("sign", "true"));

            var result = await _controller.UpdateSpendingLimit(new UpdateSpendingLimitRequest { MaxAmountPerTransaction = 1 });

            var objectResult = result as ObjectResult;
            Assert.That(objectResult!.StatusCode, Is.EqualTo(StatusCodes.Status403Forbidden));
        }
    }
}
