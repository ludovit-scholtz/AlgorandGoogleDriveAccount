using System.Security.Claims;
using BiatecMCP.BusinessLogic;
using BiatecMCP.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    /// <summary>
    /// Tests for <see cref="BiatecMCP.MCP.BiatecMCP"/> that don't require a live Algod node or BiatecOIDC -
    /// the claim-reading/scope-gating logic that runs before any network I/O. The full build-sign-broadcast
    /// path (which talks to a real Algod node via <c>HttpClientConfigurator</c>) is covered independently by
    /// <see cref="AlgorandTransactionBuilderTests"/> (transaction construction) and
    /// <see cref="BiatecWalletClientTests"/> (the BiatecOIDC wallet API call) - and, per this rewrite's
    /// verification plan, by a manual end-to-end run against a real MCP client, Algod, and BiatecOIDC.
    /// </summary>
    [TestFixture]
    public class BiatecMCPToolsTests
    {
        private Mock<IBiatecWalletClient> _walletClient = null!;
        private IHttpContextAccessor _httpContextAccessor = null!;
        private DefaultHttpContext _httpContext = null!;
        private IOptionsMonitor<AlgodConfiguration> _algodConfig = null!;

        [SetUp]
        public void SetUp()
        {
            _walletClient = new Mock<IBiatecWalletClient>();
            _httpContext = new DefaultHttpContext();
            _httpContextAccessor = Mock.Of<IHttpContextAccessor>(a => a.HttpContext == _httpContext);
            _algodConfig = Mock.Of<IOptionsMonitor<AlgodConfiguration>>(m => m.CurrentValue == new AlgodConfiguration());
        }

        private BiatecMCP.MCP.BiatecMCP CreateTool() =>
            new(_walletClient.Object, _httpContextAccessor, _algodConfig, NullLogger<BiatecMCP.MCP.BiatecMCP>.Instance);

        private void SetBearerToken(string token) =>
            _httpContext.Request.Headers.Authorization = $"Bearer {token}";

        private void SetClaims(params Claim[] claims) =>
            _httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

        [Test]
        public async Task GetAccountAddress_AlgorandAddressClaimPresent_ReturnsItWithoutCallingWalletClient()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.EqualTo("SOMEADDRESS"));
            Assert.That(result.Error, Is.Empty);
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetAccountAddress_NoClaim_FallsBackToPrimarySeedFromWalletApi()
        {
            SetClaims(); // no algorand_address claim
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse
                {
                    Seeds =
                    {
                        new SeedResponse { Address = "OLD", IsPrimary = false },
                        new SeedResponse { Address = "PRIMARY", IsPrimary = true }
                    }
                });

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.EqualTo("PRIMARY"));
        }

        [Test]
        public async Task GetAccountAddress_NoClaimAndNoSeeds_ReturnsClearError()
        {
            SetClaims();
            SetBearerToken("tok");
            _walletClient.Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>())).ReturnsAsync(new ListSeedsResponse());

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.Empty);
            Assert.That(result.Error, Does.Contain("no Algorand address"));
        }

        [Test]
        public async Task GetAccountAddress_NoClaimAndNoBearerToken_ReturnsUnauthorizedMessage()
        {
            SetClaims();
            // no bearer token set at all

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        [Test]
        public async Task TransferAsset_MissingSignClaim_ReturnsInsufficientScope_WithoutCallingWalletClient()
        {
            SetClaims(); // no "sign" claim
            SetBearerToken("tok");

            var result = await CreateTool().TransferAsset(receiverAccount: "SOME", assetId: 0, amount: 1, note: "n", genesisId: "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            Assert.That(result.Error, Does.Contain("sign"));
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task OptIn_MissingSignClaim_ReturnsInsufficientScope_WithoutCallingWalletClient()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().OptIn(assetId: 123, note: "", genesisId: "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task OptIn_ZeroAssetId_ReturnsInvalidRequest_EvenWithSignClaim()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().OptIn(assetId: 0, note: "", genesisId: "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }

        [Test]
        public async Task TransferAsset_SignClaimPresentButNoBearerToken_ReturnsUnauthorized()
        {
            // Defensive edge case: HttpContext.User carries the claim but the raw Authorization header is
            // somehow missing (shouldn't happen once JwtBearer auth has run, but this tool must not throw
            // an unhandled exception either way).
            SetClaims(new Claim("sign", "true"));

            var result = await CreateTool().TransferAsset(receiverAccount: "SOME", assetId: 0, amount: 1, note: "", genesisId: "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("Unauthorized"));
        }
    }
}
