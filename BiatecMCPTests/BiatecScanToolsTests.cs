using System.Net;
using System.Text;
using Algorand.Algod.Model;
using BiatecMCP.BusinessLogic;
using BiatecMCP.BusinessLogic.Scan;
using BiatecMCP.Helper;
using BiatecMCP.MCP;
using BiatecMCP.Model;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecMCPTests
{
    [TestFixture]
    public class PoolAprCalculatorTests
    {
        private static ScanPool Pool(double lpFee = 0.003, double portion = 0.2, double tvlA = 500, double tvlB = 500, double v24 = 100, double v7 = 700) => new()
        {
            LpFee = lpFee, ProtocolFeePortion = portion, TotalTvlAssetAInUsd = tvlA, TotalTvlAssetBInUsd = tvlB, Volume24H = v24, Volume7D = v7
        };

        [Test]
        public void Apr_IsTrailingLpFeesOverTvl_Annualised()
        {
            var pool = Pool();

            // LP fee rate 0.003 * 0.8 = 0.0024; 24h: 100 * 0.0024 / 1000 * 365 = 0.0876; 7d: 700 * 0.0024 / 1000 * 365 / 7 = 0.0876
            Assert.That(PoolAprCalculator.LpFeeRate(pool), Is.EqualTo(0.0024).Within(1e-12));
            Assert.That(PoolAprCalculator.Apr24h(pool), Is.EqualTo(0.0876).Within(1e-9));
            Assert.That(PoolAprCalculator.Apr7d(pool), Is.EqualTo(0.0876).Within(1e-9));
        }

        [Test]
        public void Apr_IsNullWithoutTvlOrFeeData()
        {
            Assert.That(PoolAprCalculator.Apr24h(Pool(tvlA: 0, tvlB: 0)), Is.Null);
            Assert.That(PoolAprCalculator.Apr24h(new ScanPool { Volume24H = 5, TotalTvlAssetAInUsd = 10 }), Is.Null, "no fee rate");
            Assert.That(PoolAprCalculator.TvlUsd(new ScanPool()), Is.Null);
        }
    }

    [TestFixture]
    public class ScanAuthAndClientTests
    {
        private static readonly AlgorandChain Chain = new() { GenesisId = "mainnet-v1.0", AlgodApiAddress = "https://algod.invalid" };

        private static TransactionParametersResponse Params() =>
            new() { MinFee = 1000, LastRound = 1000, GenesisId = "mainnet-v1.0", GenesisHash = new byte[32], ConsensusVersion = "future" };

        [Test]
        public async Task Token_IsAnArc14SigTxHeader_ReusedFromCache()
        {
            var calls = 0;
            var provider = new Arc14ScanAuthTokenProvider(new MemoryCache(new MemoryCacheOptions()), Options.Create(new ScanConfiguration()), (_, _) => { calls++; return Task.FromResult(Params()); });

            var first = await provider.GetAuthorizationHeaderAsync(Chain);
            var second = await provider.GetAuthorizationHeaderAsync(Chain);

            Assert.That(first, Does.StartWith("SigTx "));
            Assert.That(second, Is.EqualTo(first));
            Assert.That(calls, Is.EqualTo(1));

            // the proof is a zero-fee self-payment carrying the realm in its note, signed by the throwaway identity
            var signed = Algorand.Utils.Encoder.DecodeFromMsgPack<Algorand.Algod.Model.Transactions.SignedTransaction>(Convert.FromBase64String(first["SigTx ".Length..]));
            Assert.That(signed.Tx.Fee, Is.EqualTo(0UL));
            Assert.That(Encoding.UTF8.GetString(signed.Tx.Note), Is.EqualTo("BiatecScan#ARC14"));
            Assert.That(signed.Sig, Is.Not.Null);
        }

        [Test]
        public async Task Token_IsPerNetwork()
        {
            var calls = 0;
            var provider = new Arc14ScanAuthTokenProvider(new MemoryCache(new MemoryCacheOptions()), Options.Create(new ScanConfiguration()), (_, _) => { calls++; return Task.FromResult(Params()); });

            await provider.GetAuthorizationHeaderAsync(Chain);
            await provider.GetAuthorizationHeaderAsync(new AlgorandChain { GenesisId = "voimain-v1.0", AlgodApiAddress = "https://voi.invalid" });

            Assert.That(calls, Is.EqualTo(2));
        }

        private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
        {
            public List<HttpRequestMessage> Requests { get; } = new();

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Requests.Add(request);
                return Task.FromResult(respond(request));
            }
        }

        private static BiatecScanClient ClientFor(StubHandler handler)
        {
            var auth = new Mock<IScanAuthTokenProvider>();
            auth.Setup(a => a.GetAuthorizationHeaderAsync(It.IsAny<AlgorandChain>(), It.IsAny<CancellationToken>())).ReturnsAsync("SigTx abc");
            return new BiatecScanClient(new HttpClient(handler), auth.Object, Options.Create(new ScanConfiguration()));
        }

        private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

        [Test]
        public async Task Pools_AreRequestedWithTheAuthHeader_AndParsed()
        {
            var handler = new StubHandler(_ => Json("""[{"poolAppId":7,"assetIdA":1,"assetIdB":2,"protocol":"Biatec","lpFee":0.003,"protocolFeePortion":0.2,"totalTVLAssetAInUSD":10,"totalTVLAssetBInUSD":5,"volume24H":3,"volume7D":9,"scamRating":0}]"""));

            var pools = await ClientFor(handler).GetPoolsForAssetAsync("algorand-mainnet", Chain, 31566704, "Biatec", 500);

            var request = handler.Requests.Single();
            Assert.That(request.RequestUri!.ToString(), Is.EqualTo("https://api.algorand.scan.biatec.io/api/pool?assetIdA=31566704&size=500&protocol=Biatec"));
            Assert.That(request.Headers.GetValues("Authorization").Single(), Is.EqualTo("SigTx abc"));
            Assert.That(pools.Single().PoolAppId, Is.EqualTo(7UL));
            Assert.That(pools.Single().TotalTvlAssetAInUsd, Is.EqualTo(10));
        }

        [Test]
        public async Task Errors_BecomeScanApiExceptionsWithSafeMessages()
        {
            var unauthorized = ClientFor(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)));
            var down = ClientFor(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadGateway)));
            var garbage = ClientFor(new StubHandler(_ => Json("not json")));

            Assert.That((await Assert.ThrowsAsync<ScanApiException>(() => unauthorized.SearchAssetsAsync("algorand-mainnet", Chain, "x", 5)))!.Message, Does.Contain("authentication"));
            Assert.That((await Assert.ThrowsAsync<ScanApiException>(() => down.SearchAssetsAsync("algorand-mainnet", Chain, "x", 5)))!.Message, Does.Contain("502"));
            Assert.That((await Assert.ThrowsAsync<ScanApiException>(() => garbage.SearchAssetsAsync("algorand-mainnet", Chain, "x", 5)))!.Message, Does.Contain("unreadable"));
            Assert.That((await Assert.ThrowsAsync<ScanApiException>(() => down.SearchAssetsAsync("solana", Chain, "x", 5)))!.Message, Does.Contain("not available"));
        }
    }

    [TestFixture]
    public class BiatecScanMcpToolsTests
    {
        private const ulong Usdc = 31566704;
        private const ulong Eurs = 227855942;
        private static readonly AlgorandChain Chain = new() { GenesisId = "mainnet-v1.0" };

        private Mock<IBiatecScanClient> _scan = null!;
        private BiatecScanMCP _tool = null!;

        private static ScanAsset Asset(ulong id, string unit, double tvl, string name = "") =>
            new() { Index = id, Params = new ScanAssetParams { UnitName = unit, Name = name }, TvlUsd = tvl, PoolsCount = tvl > 0 ? 1 : 0 };

        private static ScanPool Pool(ulong app, ulong a, ulong b, double tvl, double v7, string protocol = "Biatec", int scam = 0, double v24 = 0) => new()
        {
            PoolAppId = app, AssetIdA = a, AssetIdB = b, Protocol = protocol, LpFee = 0.003, ProtocolFeePortion = 0,
            TotalTvlAssetAInUsd = tvl / 2, TotalTvlAssetBInUsd = tvl / 2, Volume24H = v24, Volume7D = v7, ScamRating = scam
        };

        [SetUp]
        public void SetUp()
        {
            var resolver = new Mock<INetworkResolver>();
            resolver.Setup(r => r.ResolveAsync("algorand-mainnet", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "algorand-mainnet", Family = ChainFamily.Avm, AvmChain = Chain });
            resolver.Setup(r => r.ResolveAsync("bitcoin", It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ResolvedNetwork { Code = "bitcoin", Family = ChainFamily.Btc });

            _scan = new Mock<IBiatecScanClient> { DefaultValue = DefaultValue.Empty };
            _scan.Setup(s => s.ConfiguredNetworks).Returns(new[] { "algorand-mainnet" });
            _scan.Setup(s => s.IsConfigured(It.IsAny<string>())).Returns<string>(c => c == "algorand-mainnet");
            _scan.Setup(s => s.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<AlgorandChain>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScanAsset>());
            _scan.Setup(s => s.GetAssetsByIdAsync(It.IsAny<string>(), It.IsAny<AlgorandChain>(), It.IsAny<IEnumerable<ulong>>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScanAsset>());
            _scan.Setup(s => s.GetPoolsForAssetAsync(It.IsAny<string>(), It.IsAny<AlgorandChain>(), It.IsAny<ulong>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ScanPool>());
            _scan.Setup(s => s.SearchAssetsAsync("algorand-mainnet", Chain, "EUR", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Asset(1, "EURX", 5, "Euro X"), Asset(Eurs, "EUR", 9000, "Euro"), Asset(2, "EUR", 40, "Fake Euro"), Asset(3, "EUR", 0, "Dead Euro") });
            _scan.Setup(s => s.SearchAssetsAsync("algorand-mainnet", Chain, "USDC", It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Asset(Usdc, "USDC", 1_000_000, "USDC") });

            _tool = new BiatecScanMCP(resolver.Object, _scan.Object, NullLogger<BiatecScanMCP>.Instance);
        }

        private void PoolsFor(ulong asset, params ScanPool[] pools) =>
            _scan.Setup(s => s.GetPoolsForAssetAsync("algorand-mainnet", Chain, asset, It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>())).ReturnsAsync(pools);

        [Test]
        public async Task EurUsdc_RanksPoolsOfThePairByAprDescending()
        {
            PoolsFor(Eurs,
                Pool(10, Eurs, Usdc, tvl: 10_000, v7: 7000),       // 7d APR = 0.003*7000/10000*365/7 = 10.95%
                Pool(11, Usdc, Eurs, tvl: 10_000, v7: 14_000),     // 21.9% (asset order reversed: still the pair)
                Pool(12, Eurs, 999, tvl: 10_000, v7: 99_999),      // not the pair
                Pool(13, Eurs, Usdc, tvl: 50, v7: 5000));          // below the default minimum TVL
            PoolsFor(2);

            var result = await _tool.FindBestLiquidityPools("EUR", "USDC");

            Assert.That(result.Error, Is.Empty);
            Assert.That(result.Pools.Select(p => p.PoolAppId), Is.EqualTo(new ulong[] { 11, 10 }));
            Assert.That(result.Pools[0].Apr7dPercent, Is.EqualTo(21.9).Within(1e-6));
            Assert.That(result.Pools[0].SymbolA, Is.EqualTo("USDC"));
            Assert.That(result.Pools[0].SymbolB, Is.EqualTo("EUR"));
            Assert.That(result.TotalMatchingPools, Is.EqualTo(2));
        }

        [Test]
        public async Task ASymbol_ExpandsToItsHighestTvlTokens_AndSaysSo()
        {
            PoolsFor(Eurs);
            PoolsFor(2);

            var result = await _tool.FindBestLiquidityPools("EUR", "USDC");

            // exact "EUR" matches only (not EURX), highest TVL first, the zero-TVL/zero-pool token dropped
            var eur = result.ResolvedAssets[0].Matches;
            Assert.That(eur.Select(m => m.AssetId), Is.EqualTo(new ulong[] { Eurs, 2 }));
            Assert.That(result.ResolvedAssets[1].Matches.Single().AssetId, Is.EqualTo(Usdc));
        }

        [Test]
        public async Task KnownScams_AreHiddenByDefault_AndOptInWithIncludeScam()
        {
            PoolsFor(Eurs, Pool(10, Eurs, Usdc, 10_000, 7000, scam: 95), Pool(11, Eurs, Usdc, 10_000, 1000, scam: 5));
            PoolsFor(2);

            var hidden = await _tool.FindBestLiquidityPools("EUR", "USDC");
            var shown = await _tool.FindBestLiquidityPools("EUR", "USDC", includeScam: true);

            Assert.That(hidden.Pools.Select(p => p.PoolAppId), Is.EqualTo(new ulong[] { 11 }));
            Assert.That(hidden.Pools[0].ScamRating, Is.EqualTo(5));
            Assert.That(shown.Pools.Select(p => p.PoolAppId), Is.EqualTo(new ulong[] { 10, 11 }));
        }

        [Test]
        public async Task SortByTvl_AndLimit_AndMinTvl_Apply()
        {
            PoolsFor(Usdc,
                Pool(1, Usdc, 5, tvl: 1_000, v7: 100),
                Pool(2, Usdc, 6, tvl: 9_000, v7: 100),
                Pool(3, Usdc, 7, tvl: 5_000, v7: 100));
            _scan.Setup(s => s.GetAssetsByIdAsync("algorand-mainnet", Chain, It.IsAny<IEnumerable<ulong>>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Asset(6, "SIX", 1), Asset(7, "SEVEN", 1) });

            var result = await _tool.FindBestLiquidityPools("USDC", sortBy: "tvl", minTvlUsd: 2000, limit: 1);

            Assert.That(result.Pools.Select(p => p.PoolAppId), Is.EqualTo(new ulong[] { 2 }));
            Assert.That(result.Pools[0].SymbolB, Is.EqualTo("SIX"), "the other side's symbol is looked up");
            Assert.That(result.TotalMatchingPools, Is.EqualTo(2));
        }

        [Test]
        public async Task BadInput_IsRejectedBeforeAnyScanCall()
        {
            Assert.That((await _tool.FindBestLiquidityPools("EUR", sortBy: "luck")).ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That((await _tool.FindBestLiquidityPools("EUR", protocol: "uniswap")).ErrorType, Is.EqualTo("InvalidRequest"));
            Assert.That((await _tool.FindBestLiquidityPools(" ")).ErrorType, Is.EqualTo("InvalidRequest"));
            _scan.Verify(s => s.GetPoolsForAssetAsync(It.IsAny<string>(), It.IsAny<AlgorandChain>(), It.IsAny<ulong>(), It.IsAny<string?>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        [Test]
        public async Task UnknownAsset_OrUnsupportedNetwork_GiveClearErrors()
        {
            var missing = await _tool.FindBestLiquidityPools("EUR", "NOPE");
            Assert.That(missing.ErrorType, Is.EqualTo("AssetNotFound"));
            Assert.That(missing.Error, Does.Contain("NOPE"));

            var wrongNetwork = await _tool.FindBestLiquidityPools("EUR", network: "bitcoin");
            Assert.That(wrongNetwork.ErrorType, Is.EqualTo("ScanUnavailable"));
            Assert.That(wrongNetwork.Error, Does.Contain("algorand-mainnet"));
        }

        [Test]
        public async Task ScanOutage_IsReportedNotCrashed()
        {
            _scan.Setup(s => s.SearchAssetsAsync(It.IsAny<string>(), It.IsAny<AlgorandChain>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ThrowsAsync(new ScanApiException("Biatec Scan could not be reached."));

            var result = await _tool.FindBestLiquidityPools("EUR", "USDC");

            Assert.That(result.ErrorType, Is.EqualTo("ScanUnavailable"));
            Assert.That(result.Pools, Is.Empty);
        }

        [Test]
        public async Task TopAssetsByApr_FiltersToCombinedRows_RanksAndKeepsPercentUnits()
        {
            _scan.Setup(s => s.GetAssetStatsAsync("algorand-mainnet", Chain, null, "Apr7d", It.IsAny<CancellationToken>())).ReturnsAsync(new[]
            {
                new ScanAssetStat { AssetId = 1, UnitName = "A", TvlUsd = 50_000, Apr7d = 12.5, Apr24h = 3 },
                new ScanAssetStat { AssetId = 1, Protocol = "Tiny", UnitName = "A", TvlUsd = 40_000, Apr7d = 99 },
                new ScanAssetStat { AssetId = 2, UnitName = "B", TvlUsd = 60_000, Apr7d = 20, Apr24h = 1 },
                new ScanAssetStat { AssetId = 3, UnitName = "C", TvlUsd = 10, Apr7d = 5000 }
            });

            var result = await _tool.GetBiatecScanTopAssetsByApr();

            Assert.That(result.Assets.Select(a => a.AssetId), Is.EqualTo(new ulong[] { 2, 1 }));
            Assert.That(result.Assets[0].Apr7dPercent, Is.EqualTo(20));
        }

        [Test]
        public async Task SearchAssets_ByIdAndText()
        {
            _scan.Setup(s => s.GetAssetsByIdAsync("algorand-mainnet", Chain, It.Is<IEnumerable<ulong>>(i => i.Single() == Usdc), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new[] { Asset(Usdc, "USDC", 100) });

            var byId = await _tool.SearchBiatecScanAssets("31566704");
            var byText = await _tool.SearchBiatecScanAssets("EUR", limit: 2);

            Assert.That(byId.Assets.Single().Symbol, Is.EqualTo("USDC"));
            Assert.That(byText.Assets.Select(a => a.AssetId), Is.EqualTo(new ulong[] { Eurs, 2 }));
        }
    }
}
