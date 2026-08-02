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
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task OptIn_MissingSignClaim_ReturnsInsufficientScope_WithoutCallingWalletClient()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().OptIn(assetId: 123, note: "", genesisId: "mainnet-v1.0");

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
            _walletClient.Verify(c => c.SignAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
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

        // ───────────────────────── Multi-address (ARC-76 slot) support ─────────────────────────

        [Test]
        public async Task GetAccountAddress_DefaultSlotAndNoPrimaryAddress_UsesClaimFastPath()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS"));

            var result = await CreateTool().GetAccountAddress();

            Assert.That(result.Address, Is.EqualTo("SOMEADDRESS"));
            _walletClient.Verify(c => c.GetAddressAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task GetAccountAddress_NonZeroSlot_DerivesFromPrimarySeedViaWalletApi()
        {
            SetClaims(new Claim("algorand_address", "SOMEADDRESS")); // claim present but ignored - slot != 0
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListSeedsAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListSeedsResponse { Seeds = { new SeedResponse { Address = "PRIMARY", IsPrimary = true } } });
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "PRIMARY", 1, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "SECOND-ADDRESS", PrimaryAddress = "PRIMARY", Slot = 1 });

            var result = await CreateTool().GetAccountAddress(slot: 1);

            Assert.That(result.Address, Is.EqualTo("SECOND-ADDRESS"));
        }

        [Test]
        public async Task GetAccountAddress_ExplicitPrimaryAddress_DerivesFromThatSeedViaWalletApi()
        {
            SetClaims();
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "OTHER-SEED", 0, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "OTHER-DERIVED", PrimaryAddress = "OTHER-SEED", Slot = 0 });

            var result = await CreateTool().GetAccountAddress(primaryAddress: "OTHER-SEED");

            Assert.That(result.Address, Is.EqualTo("OTHER-DERIVED"));
            _walletClient.Verify(c => c.ListSeedsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task ListAlgorandAddresses_ReturnsAddressesFromWalletClient()
        {
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.ListAddressesAsync("tok", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ListAddressesResponse
                {
                    Addresses = { new AddressResponse { Address = "A1", IsPrimary = true }, new AddressResponse { Address = "A2", IsPrimary = false } }
                });

            var result = await CreateTool().ListAlgorandAddresses();

            Assert.That(result.Addresses, Has.Count.EqualTo(2));
            Assert.That(result.Error, Is.Empty);
        }

        [Test]
        public async Task ListAlgorandAddresses_NoBearerToken_ReturnsError()
        {
            var result = await CreateTool().ListAlgorandAddresses();

            Assert.That(result.Error, Does.Contain("bearer token"));
        }

        [Test]
        public async Task TransferAsset_WithPrimaryAddressAndSlot_ResolvesSenderViaWalletApi()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");
            _walletClient
                .Setup(c => c.GetAddressAsync("tok", "OTHER-SEED", 10, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DerivedAddressResponse { Address = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAY5HFKQ", PrimaryAddress = "OTHER-SEED", Slot = 10 });

            // No Algod network is configured for "mainnet-v1.0" in this test's AlgodConfiguration, so the
            // call fails deterministically once it reaches that stage - proving the sender was resolved
            // through the wallet API first (an unconfigured genesisId throws ArgumentException).
            var result = await CreateTool().TransferAsset(receiverAccount: "SOME", assetId: 0, amount: 1, note: "n", genesisId: "mainnet-v1.0", primaryAddress: "OTHER-SEED", slot: 10);

            _walletClient.Verify(c => c.GetAddressAsync("tok", "OTHER-SEED", 10, It.IsAny<CancellationToken>()), Times.Once);
            Assert.That(result.ErrorType, Does.Contain("ArgumentException"));
        }

        [Test]
        public async Task ExecuteTransaction_MissingSignClaim_ReturnsInsufficientScope()
        {
            SetClaims();
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string> { "AA==" });

            Assert.That(result.ErrorType, Is.EqualTo("InsufficientScope"));
        }

        [Test]
        public async Task ExecuteTransaction_EmptyList_ReturnsInvalidRequest()
        {
            SetClaims(new Claim("sign", "true"));
            SetBearerToken("tok");

            var result = await CreateTool().ExecuteTransaction(new List<string>());

            Assert.That(result.ErrorType, Is.EqualTo("InvalidRequest"));
        }
    }
}
