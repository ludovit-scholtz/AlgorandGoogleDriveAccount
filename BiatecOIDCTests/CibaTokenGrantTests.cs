using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecOIDCTests
{
    /// <summary>The elevated token itself, issued by the real signing service and validated by the same service the wallet API uses.</summary>
    [TestFixture]
    public class ConsentAccessTokenTests : JwtIssuerServiceTestBase
    {
        private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

        private static List<Claim> Challenged() => new()
        {
            new Claim("sub", "user-1"),
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim(ClaimTypes.Name, "user"),
            new Claim("scope", "openid sign"),
            new Claim("sign", "true"),
            new Claim("primary_seed_address", "SEEDADDR"),
            new Claim(AuthSchemeNames.IdpClaimType, "google"),
            new Claim(ProviderAccessTokenProtector.ClaimType, "protected:abc"),
        };

        /// <summary>The raw claims of the token, as the wallet API and the consent endpoints read them.</summary>
        private IDictionary<string, object> Validate(string token)
        {
            var result = Service.ValidateBearerAccessToken(token);
            Assert.That(result.IsValid, Is.True, result.Error);
            return result.Claims!;
        }

        [Test]
        public void TheElevatedToken_CarriesTheChallengedTokensClaimsAndTheConsentClaims_AndValidatesLikeAnyOtherToken()
        {
            var token = Service.IssueConsentAccessToken(Challenged(), TestClientId, "https://mcp.biatec.io/tools", "ciba_abc", "HASH-1", Now, TimeSpan.FromSeconds(300));
            var claims = Validate(token);

            Assert.That(claims["sub"], Is.EqualTo("user-1"));
            Assert.That(claims[ClaimTypes.Email], Is.EqualTo("user@example.com"));
            Assert.That(claims["sign"], Is.EqualTo("true"));
            Assert.That(claims["primary_seed_address"], Is.EqualTo("SEEDADDR"));
            Assert.That(claims[ProviderAccessTokenProtector.ClaimType], Is.EqualTo("protected:abc"));
            Assert.That(claims["acr"], Is.EqualTo("biatec:consent"));
            Assert.That(claims[ConsentTokenClaims.AuthReqIdClaim], Is.EqualTo("ciba_abc"));
            Assert.That(claims[ConsentTokenClaims.HashClaim], Is.EqualTo("HASH-1"));
            Assert.That(claims["client_id"], Is.EqualTo(TestClientId));
            var jwtAudiences = new JwtSecurityTokenHandler().ReadJwtToken(token).Audiences;
            Assert.That(jwtAudiences, Is.EquivalentTo(new[] { TestClientId, "https://mcp.biatec.io/tools" }));

            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            Assert.That(jwt.ValidTo - jwt.ValidFrom, Is.EqualTo(TimeSpan.FromSeconds(300)).Within(TimeSpan.FromSeconds(2)));
        }

        [Test]
        public void OnlyAllowlistedClaimsTravel_AndTheConsentClaimsCannotBeForgedFromTheSnapshot()
        {
            var claims = Challenged();
            claims.AddRange(new[]
            {
                new Claim("acr", "forged"), new Claim(ConsentTokenClaims.HashClaim, "FORGED"), new Claim(ConsentTokenClaims.AuthReqIdClaim, "ciba_forged"),
                new Claim("client_id", "another-client"), new Claim("role", "admin"), new Claim("iss", "https://evil.test"), new Claim("exp", "9999999999"),
                new Claim("token_use", "refresh_token"), new Claim("jti", "fixed"),
            });
            var token = Service.IssueConsentAccessToken(claims, TestClientId, null, "ciba_abc", "HASH-1", Now, TimeSpan.FromSeconds(300));
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
            Validate(token);

            Assert.That(jwt.Claims.Where(c => c.Type == "acr").Select(c => c.Value), Is.EqualTo(new[] { "biatec:consent" }));
            Assert.That(jwt.Claims.Where(c => c.Type == ConsentTokenClaims.HashClaim).Select(c => c.Value), Is.EqualTo(new[] { "HASH-1" }));
            Assert.That(jwt.Claims.Where(c => c.Type == ConsentTokenClaims.AuthReqIdClaim).Select(c => c.Value), Is.EqualTo(new[] { "ciba_abc" }));
            Assert.That(jwt.Claims.Where(c => c.Type == "client_id").Select(c => c.Value), Is.EqualTo(new[] { TestClientId }));
            Assert.That(jwt.Claims.Any(c => c.Type == "role"), Is.False);
            Assert.That(jwt.Claims.Single(c => c.Type == "token_use").Value, Is.EqualTo("access_token"));
            Assert.That(jwt.Claims.Single(c => c.Type == "jti").Value, Is.Not.EqualTo("fixed"));
            Assert.That(jwt.Issuer, Is.EqualTo(TestIssuer));
            Assert.That(jwt.ValidTo, Is.LessThan(DateTime.UtcNow.AddMinutes(10)));
        }

        [Test]
        public void TheSnapshot_TakesOnlyAllowlistedClaims_FromTheRawClaimsOfARealToken()
        {
            // A real access token, challenged at the wallet endpoint: its raw claims are what get stored.
            var original = Service.IssueConsentAccessToken(Challenged(), TestClientId, null, "ciba_x", "H", Now, TimeSpan.FromMinutes(5));
            var snapshot = ConsentTokenClaims.Snapshot(Validate(original));
            Assert.That(snapshot.Select(c => c.Type), Is.SupersetOf(new[] { "sub", ClaimTypes.Email, "sign", "primary_seed_address", ProviderAccessTokenProtector.ClaimType }));
            Assert.That(snapshot.Select(c => c.Type), Has.None.EqualTo("client_id").And.None.EqualTo("jti").And.None.EqualTo("acr").And.None.EqualTo("exp").And.None.EqualTo("aud"));

            // And re-issuing from the snapshot gives a token with the same identity claims, written the same way.
            var again = Service.IssueConsentAccessToken(snapshot.Select(c => new Claim(c.Type, c.Value)), TestClientId, null, "ciba_y", "H2", Now, TimeSpan.FromMinutes(5));
            var claims = Validate(again);
            Assert.That(claims["sub"], Is.EqualTo("user-1"));
            Assert.That(claims[ClaimTypes.Email], Is.EqualTo("user@example.com"));
            Assert.That(claims[ConsentTokenClaims.HashClaim], Is.EqualTo("H2"));
        }

        [Test]
        public async Task RealTokens_IdentifyTheUser_AtTheDeviceAndReceiptEndpoints()
        {
            // Regression: the validated principal maps "sub" to another claim type, so reading it from the principal made every real token fail.
            var token = Service.IssueConsentAccessToken(Challenged(), TestClientId, null, "ciba_abc", "H", Now, TimeSpan.FromMinutes(5));

            var store = new FakeDeviceStore();
            var devices = new DevicesController(store, Service, Options.Create(new DevicesConfiguration()), TimeProvider.System,
                new DeviceAuthenticator(store, Service, Options.Create(new DevicesConfiguration()), TimeProvider.System));
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = "Bearer " + token;
            devices.ControllerContext = new ControllerContext { HttpContext = http };
            using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
            var p = key.ExportParameters(false);
            var enrolled = await devices.Enroll(new EnrollDeviceRequest
            {
                Kid = "device-0001", PublicKey = new DeviceJwk { Kty = "EC", Crv = "P-256", X = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Q.X!), Y = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(p.Q.Y!) },
            });
            Assert.That(((ObjectResult)enrolled).StatusCode, Is.EqualTo(201));
            Assert.That(store.Devices["device-0001"].Sub, Is.EqualTo("user-1"));

            ReceiptCaller? seen = null;
            var consent = new Mock<IConsentService>();
            consent.Setup(s => s.GetReceiptAsync(It.IsAny<ReceiptCaller>(), "ciba_1")).Callback<ReceiptCaller, string>((c, _) => seen = c).ReturnsAsync((ReceiptLookup.NotFound, (string?)null));
            var controller = new ConsentController(consent.Object, Mock.Of<IDeviceAuthenticator>(), Service, TimeProvider.System);
            var http2 = new DefaultHttpContext();
            http2.Request.Headers.Authorization = "Bearer " + token;
            controller.ControllerContext = new ControllerContext { HttpContext = http2 };
            await controller.GetReceipt("ciba_1");
            Assert.That((seen!.Sub, seen.ClientId, seen.ClientAuthenticated), Is.EqualTo(("user-1", TestClientId, false)));
        }
    }

    [TestFixture]
    public class CibaTokenGrantTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
        private static readonly JwtIssuerClientConfiguration Client = new() { ClientId = "chat", ClientSecret = "secret" };

        private Mock<IConsentService> _consent = null!;
        private Mock<IJwtIssuerService> _jwt = null!;
        private CibaTokenGrant _grant = null!;

        [SetUp]
        public void SetUp()
        {
            _consent = new Mock<IConsentService>();
            _jwt = new Mock<IJwtIssuerService>();
            _jwt.Setup(j => j.IssueConsentAccessToken(It.IsAny<IEnumerable<Claim>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>()))
                .Returns("elevated.jwt.token");
            _grant = new CibaTokenGrant(_consent.Object, _jwt.Object, Options.Create(new ConsentConfiguration()), new FixedTimeProvider(Now));
        }

        private void Poll(PollOutcome outcome, int interval = 2, ConsentRequestRecord? request = null)
            => _consent.Setup(s => s.PollAsync("chat", "ciba_1", It.IsAny<string?>())).ReturnsAsync(new PollResult { Outcome = outcome, Interval = interval, Request = request });

        private static ConsentRequestRecord Approved() => new()
        {
            AuthReqId = "ciba_1", ClientId = "chat", Scope = "sign", Resource = "https://mcp.biatec.io/tools", BoundHash = "HASH-1",
            ReceiptJson = "{\"version\":\"1\",\"signed\":{\"decision\":\"approve\"}}",
            TokenClaims = new List<ClaimSnapshot> { new() { Type = "sub", Value = "user-1" } },
        };

        [TestCase(PollOutcome.Pending, "authorization_pending", 2)]
        [TestCase(PollOutcome.SlowDown, "slow_down", 7)]
        public async Task PendingAndSlowDown_StateTheIntervalToObserve(PollOutcome outcome, string error, int interval)
        {
            Poll(outcome, interval);
            var r = await _grant.RedeemAsync(Client, "ciba_1", null);
            Assert.That((r.Status, r.Error, r.Interval), Is.EqualTo((400, error, (int?)interval)));
            Assert.That(r.Body, Is.Null);
        }

        [TestCase(PollOutcome.AccessDenied, "access_denied")]
        [TestCase(PollOutcome.Expired, "expired_token")]
        [TestCase(PollOutcome.InvalidTarget, "invalid_target")]
        [TestCase(PollOutcome.InvalidGrant, "invalid_grant")]
        public async Task EveryOtherOutcome_IsAnOAuthError_WithoutAToken(PollOutcome outcome, string error)
        {
            Poll(outcome);
            var r = await _grant.RedeemAsync(Client, "ciba_1", null);
            Assert.That((r.Status, r.Error), Is.EqualTo((400, error)));
            Assert.That(r.Body, Is.Null);
            _jwt.Verify(j => j.IssueConsentAccessToken(It.IsAny<IEnumerable<Claim>>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<TimeSpan>()), Times.Never);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public async Task AMissingAuthReqId_IsAnInvalidRequest_AndNothingIsPolled(string? id)
        {
            var r = await _grant.RedeemAsync(Client, id, null);
            Assert.That((r.Status, r.Error), Is.EqualTo((400, "invalid_request")));
            _consent.VerifyNoOtherCalls();
            Assert.That((await _grant.RedeemAsync(Client, new string('x', 129), null)).Error, Is.EqualTo("invalid_request"));
        }

        [Test]
        public async Task AnApproval_YieldsTheElevatedTokenAndTheStoredReceipt_AndNoRefreshOrIdToken()
        {
            Poll(PollOutcome.Approved, request: Approved());
            var r = await _grant.RedeemAsync(Client, "ciba_1", "https://mcp.biatec.io/tools");
            Assert.That(r.Status, Is.EqualTo(200));
            var body = r.Body!;
            Assert.That(body["access_token"]!.GetValue<string>(), Is.EqualTo("elevated.jwt.token"));
            Assert.That(body["token_type"]!.GetValue<string>(), Is.EqualTo("Bearer"));
            Assert.That(body["expires_in"]!.GetValue<int>(), Is.EqualTo(300));
            Assert.That(body["scope"]!.GetValue<string>(), Is.EqualTo("sign"));
            Assert.That(body["consent_receipt"]!["signed"]!["decision"]!.GetValue<string>(), Is.EqualTo("approve"));
            Assert.That(body.ContainsKey("refresh_token"), Is.False);
            Assert.That(body.ContainsKey("id_token"), Is.False);
            _jwt.Verify(j => j.IssueConsentAccessToken(It.Is<IEnumerable<Claim>>(c => c.Single().Type == "sub"), "chat", "https://mcp.biatec.io/tools", "ciba_1", "HASH-1", Now, TimeSpan.FromSeconds(300)), Times.Once);
            _consent.Verify(s => s.PollAsync("chat", "ciba_1", "https://mcp.biatec.io/tools"), Times.Once);
        }

        // ---- the token endpoint ----

        private JwtIssuerController Controller(IFormCollection form, string? authorization = null, bool withGrant = true)
        {
            var mockConfig = Mock.Of<IOptionsMonitor<MockCloudServiceConfiguration>>(m => m.CurrentValue == new MockCloudServiceConfiguration());
            var grant = new Mock<ICibaTokenGrant>();
            grant.Setup(g => g.RedeemAsync(It.IsAny<JwtIssuerClientConfiguration>(), It.IsAny<string?>(), It.IsAny<string?>()))
                .ReturnsAsync((JwtIssuerClientConfiguration _, string? id, string? res) => id == "pending"
                    ? new CibaGrantResult { Status = 400, Error = "authorization_pending", Description = "wait", Interval = 2 }
                    : new CibaGrantResult { Status = 200, Body = new JsonObject { ["access_token"] = "t", ["resource_seen"] = res } });
            var c = new JwtIssuerController(_jwt.Object, Mock.Of<Microsoft.Extensions.Caching.Distributed.IDistributedCache>(),
                Mock.Of<BiatecSelfCustodyCore.Providers.ICloudStorageProviderCatalog>(), Mock.Of<BiatecSelfCustodyCore.Repository.ICloudAccountRepository>(), mockConfig,
                null, withGrant ? grant.Object : null);
            var http = new DefaultHttpContext();
            http.Request.ContentType = "application/x-www-form-urlencoded";
            http.Request.Form = form;
            if (authorization is not null) http.Request.Headers.Authorization = authorization;
            c.ControllerContext = new ControllerContext { HttpContext = http };
            return c;
        }

        private static FormCollection Form(params (string, string)[] fields)
            => new(fields.ToDictionary(f => f.Item1, f => new Microsoft.Extensions.Primitives.StringValues(f.Item2)));

        [Test]
        public async Task TheTokenEndpoint_RefusesAClientThatDoesNotAuthenticate_With401()
        {
            _jwt.Setup(j => j.AuthenticateConfidentialClientAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync((JwtIssuerClientConfiguration?)null);
            var c = Controller(Form(("grant_type", ICibaTokenGrant.GrantType), ("auth_req_id", "x")));
            var r = (ObjectResult)await c.Token();
            Assert.That(r.StatusCode, Is.EqualTo(401));
            Assert.That(JsonSerializer.Serialize(r.Value), Does.Contain("invalid_client"));
            Assert.That(c.Response.Headers.WWWAuthenticate.ToString(), Does.StartWith("Basic"));
        }

        [Test]
        public async Task TheTokenEndpoint_PassesTheAuthenticatedClientThrough_AndAnswersWithNoStore()
        {
            _jwt.Setup(j => j.AuthenticateConfidentialClientAsync("Basic abc", "chat", "s")).ReturnsAsync(Client);
            var c = Controller(Form(("grant_type", ICibaTokenGrant.GrantType), ("auth_req_id", "ciba_1"), ("client_id", "chat"), ("client_secret", "s"), ("resource", "https://r.test")), "Basic abc");
            var ok = (OkObjectResult)await c.Token();
            Assert.That(JsonSerializer.Serialize(ok.Value), Does.Contain("\"access_token\":\"t\"").And.Contain("https://r.test"));
            Assert.That(c.Response.Headers.CacheControl.ToString(), Is.EqualTo("no-store"));
        }

        [Test]
        public async Task TheTokenEndpoint_ReportsPendingWithTheInterval()
        {
            _jwt.Setup(j => j.AuthenticateConfidentialClientAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>())).ReturnsAsync(Client);
            var r = (ObjectResult)await Controller(Form(("grant_type", ICibaTokenGrant.GrantType), ("auth_req_id", "pending"))).Token();
            Assert.That(r.StatusCode, Is.EqualTo(400));
            var text = JsonSerializer.Serialize(r.Value);
            Assert.That(text, Does.Contain("authorization_pending").And.Contain("\"interval\":2"));
        }

        [Test]
        public async Task TheTokenEndpoint_WithoutTheGrantConfigured_SaysUnsupported_AndOtherGrantsAreUntouched()
        {
            var none = (ObjectResult)await Controller(Form(("grant_type", ICibaTokenGrant.GrantType), ("auth_req_id", "x")), withGrant: false).Token();
            Assert.That(JsonSerializer.Serialize(none.Value), Does.Contain("unsupported_grant_type"));

            _jwt.Setup(j => j.ExchangeTokenAsync(It.IsAny<OidcTokenRequest>(), It.IsAny<string?>()))
                .ReturnsAsync((true, 200, (string?)null, (string?)null, new OidcTokenResponse { AccessToken = "regular" }));
            var regular = (OkObjectResult)await Controller(Form(("grant_type", "authorization_code"), ("code", "c"))).Token();
            Assert.That(((OidcTokenResponse)regular.Value!).AccessToken, Is.EqualTo("regular"));
            _jwt.Verify(j => j.AuthenticateConfidentialClientAsync(It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<string?>()), Times.Never);
        }
    }
}
