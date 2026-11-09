using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;

namespace BiatecOIDCTests
{
    /// <summary>The HTTP contract of the consent endpoints: status codes, challenges and who may read a receipt.</summary>
    [TestFixture]
    public class ConsentControllerTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

        private Mock<IConsentService> _consent = null!;
        private Mock<IDeviceAuthenticator> _devices = null!;
        private Mock<IJwtIssuerService> _jwt = null!;
        private DeviceRecord _device = null!;

        [SetUp]
        public void SetUp()
        {
            _consent = new Mock<IConsentService>();
            _devices = new Mock<IDeviceAuthenticator>();
            _jwt = new Mock<IJwtIssuerService>();
            _jwt.Setup(j => j.GetIssuer(It.IsAny<HttpRequest>())).Returns("https://oidc.test");
            _device = new DeviceRecord { Kid = "device-0001", Sub = "user-1", ActiveAt = Now.ToUnixTimeSeconds() - 10 };
            _devices.Setup(d => d.AuthenticateAsync(It.IsAny<HttpRequest>())).ReturnsAsync((_device, DeviceAuthFailure.None));
        }

        private ConsentController Controller(string? authorization = null)
        {
            var c = new ConsentController(_consent.Object, _devices.Object, _jwt.Object, new FixedTimeProvider(Now));
            var http = new DefaultHttpContext();
            if (authorization is not null) http.Request.Headers.Authorization = authorization;
            c.ControllerContext = new ControllerContext { HttpContext = http };
            return c;
        }

        private static int Status(IActionResult r) => r switch
        {
            ObjectResult o => o.StatusCode ?? 200,
            ContentResult c => c.StatusCode ?? 200,
            StatusCodeResult s => s.StatusCode,
            _ => throw new AssertionException(r.GetType().Name),
        };

        // ---- device authentication ----

        [TestCase(DeviceAuthFailure.InvalidToken, "Device error=\"invalid_token\"")]
        [TestCase(DeviceAuthFailure.InvalidProof, "Device error=\"invalid_proof\"")]
        public async Task EveryDeviceEndpoint_Refuses_AnUnauthenticatedDevice(DeviceAuthFailure failure, string challenge)
        {
            _devices.Setup(d => d.AuthenticateAsync(It.IsAny<HttpRequest>())).ReturnsAsync(((DeviceRecord?)null, failure));
            foreach (var call in new Func<ConsentController, Task<IActionResult>>[]
            {
                c => c.ListRequests(), c => c.GetRequest("ciba_1"), c => c.PostReceipt("ciba_1", JsonDocument.Parse("{}").RootElement),
            })
            {
                var c = Controller();
                Assert.That(Status(await call(c)), Is.EqualTo(401));
                Assert.That(c.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo(challenge));
            }
            _consent.VerifyNoOtherCalls();
        }

        [Test]
        public async Task EveryDeviceEndpoint_Refuses_ADeviceThatIsNotActiveYet_WithoutLeakingAnything()
        {
            _device.ActiveAt = Now.ToUnixTimeSeconds() + 60;
            foreach (var call in new Func<ConsentController, Task<IActionResult>>[]
            {
                c => c.ListRequests(), c => c.GetRequest("ciba_1"), c => c.PostReceipt("ciba_1", JsonDocument.Parse("{}").RootElement),
            })
            {
                var r = (ObjectResult)await call(Controller());
                Assert.That(r.StatusCode, Is.EqualTo(403));
                Assert.That(JsonSerializer.Serialize(r.Value), Does.Contain("device_inactive"));
            }
            _consent.VerifyNoOtherCalls();
        }

        [Test]
        public void TheServiceGraph_ResolvesWithScopeValidation()
        {
            var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, typeof(Microsoft.Extensions.Logging.ILogger<>), typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger<>));
            Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.AddOptions(services);
            Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.Configure<ConsentConfiguration>(services, _ => { });
            Microsoft.Extensions.DependencyInjection.OptionsServiceCollectionExtensions.Configure<DevicesConfiguration>(services, _ => { });
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, TimeProvider.System);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, Mock.Of<StackExchange.Redis.IConnectionMultiplexer>());
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton(services, _jwt.Object);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddSingleton<IConsentAttestationService, ConsentAttestationService>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IDeviceStore, DeviceStore>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IDeviceAuthenticator, DeviceAuthenticator>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IConsentStore, ConsentStore>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<IConsentService, ConsentService>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<ConsentController>(services);
            Microsoft.Extensions.DependencyInjection.ServiceCollectionServiceExtensions.AddScoped<DevicesController>(services);
            using var provider = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services,
                new Microsoft.Extensions.DependencyInjection.ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
            using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateScope(provider);
            Assert.That(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<ConsentController>(scope.ServiceProvider), Is.Not.Null);
            Assert.That(Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<DevicesController>(scope.ServiceProvider), Is.Not.Null);
        }

        [Test]
        public void ConsentAndDeviceResponses_AreNeverCached()
        {
            foreach (var type in new[] { typeof(ConsentController), typeof(DevicesController) })
            {
                var attr = type.GetCustomAttributes(typeof(ResponseCacheAttribute), true).Cast<ResponseCacheAttribute>().SingleOrDefault();
                Assert.That(attr, Is.Not.Null, type.Name);
                Assert.That(attr!.NoStore, Is.True, type.Name);
            }
        }

        // ---- requests ----

        [Test]
        public async Task ListRequests_ReturnsTheServicesViews()
        {
            _consent.Setup(s => s.ListOpenForDeviceAsync(_device)).ReturnsAsync(new[] { new JsonObject { ["auth_req_id"] = "ciba_1" } });
            var r = (OkObjectResult)await Controller().ListRequests();
            Assert.That(JsonSerializer.Serialize(r.Value), Does.Contain("ciba_1").And.Contain("requests"));
        }

        [Test]
        public async Task GetRequest_Is404_ForAnUnknownOrForeignRequest_AndOkOtherwise()
        {
            _consent.Setup(s => s.GetForDeviceAsync(_device, "ciba_1")).ReturnsAsync(new JsonObject { ["auth_req_id"] = "ciba_1" });
            _consent.Setup(s => s.GetForDeviceAsync(_device, "ciba_x")).ReturnsAsync((JsonObject?)null);
            Assert.That(Status(await Controller().GetRequest("ciba_1")), Is.EqualTo(200));
            Assert.That(Status(await Controller().GetRequest("ciba_x")), Is.EqualTo(404));
        }

        // ---- receipts: the status of every outcome ----

        [TestCase(ReceiptOutcome.Approved, 200, "approved")]
        [TestCase(ReceiptOutcome.Denied, 200, "denied")]
        [TestCase(ReceiptOutcome.CodeFailedRecorded, 200, "code_failed")]
        [TestCase(ReceiptOutcome.WrongCode, 400, "wrong_code")]
        [TestCase(ReceiptOutcome.CodeLocked, 403, "access_denied")]
        [TestCase(ReceiptOutcome.NotFound, 404, "not_found")]
        [TestCase(ReceiptOutcome.AlreadyDecided, 409, "already_decided")]
        [TestCase(ReceiptOutcome.Conflict, 409, "\"conflict\"")]
        [TestCase(ReceiptOutcome.Expired, 410, "expired")]
        [TestCase(ReceiptOutcome.DeviceInactive, 403, "device_inactive")]
        [TestCase(ReceiptOutcome.NotConfigured, 503, "consent_not_configured")]
        [TestCase(ReceiptOutcome.InvalidReceipt, 400, "invalid_receipt")]
        public async Task PostReceipt_MapsEveryOutcome(ReceiptOutcome outcome, int status, string body)
        {
            _consent.Setup(s => s.SubmitReceiptAsync(_device, "ciba_1", It.IsAny<JsonElement>(), "https://oidc.test"))
                .ReturnsAsync(new ReceiptResult { Outcome = outcome, AttemptsRemaining = 2, Detail = "bad_signature" });
            var r = (ObjectResult)await Controller().PostReceipt("ciba_1", JsonDocument.Parse("{}").RootElement);
            Assert.That(r.StatusCode, Is.EqualTo(status));
            Assert.That(JsonSerializer.Serialize(r.Value), Does.Contain(body));
        }

        [Test]
        public async Task PostReceipt_WrongCode_ReportsTheAttemptsLeft_AndLockedSaysCodeFailed()
        {
            _consent.Setup(s => s.SubmitReceiptAsync(It.IsAny<DeviceRecord>(), It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<string>()))
                .ReturnsAsync(new ReceiptResult { Outcome = ReceiptOutcome.WrongCode, AttemptsRemaining = 2 });
            Assert.That(JsonSerializer.Serialize(((ObjectResult)await Controller().PostReceipt("ciba_1", JsonDocument.Parse("{}").RootElement)).Value), Does.Contain("\"attempts_remaining\":2"));
            _consent.Setup(s => s.SubmitReceiptAsync(It.IsAny<DeviceRecord>(), It.IsAny<string>(), It.IsAny<JsonElement>(), It.IsAny<string>()))
                .ReturnsAsync(new ReceiptResult { Outcome = ReceiptOutcome.CodeLocked });
            Assert.That(JsonSerializer.Serialize(((ObjectResult)await Controller().PostReceipt("ciba_1", JsonDocument.Parse("{}").RootElement)).Value), Does.Contain("\"reason\":\"code_failed\""));
        }

        // ---- receipts: retrieval ----

        private void BearerFor(string token, string? sub, string? clientId, bool valid = true)
        {
            var claims = new List<Claim>();
            if (sub is not null) claims.Add(new Claim("sub", sub));
            if (clientId is not null) claims.Add(new Claim("client_id", clientId));
            _jwt.Setup(j => j.ValidateBearerAccessToken(token)).Returns((valid, valid ? new ClaimsPrincipal(new ClaimsIdentity(claims, "t")) : null, null, valid ? null : "x"));
        }

        [Test]
        public async Task GetReceipt_WithNoCredentials_Is401WithAChallenge()
        {
            foreach (var header in new string?[] { null, "", "Bearer ", "Bearer junk", "Basic bad", "Device x" })
            {
                _jwt.Setup(j => j.AuthenticateConfidentialClientAsync(It.IsAny<string?>())).ReturnsAsync((JwtIssuerClientConfiguration?)null);
                BearerFor("junk", null, null, valid: false);
                var c = Controller(header);
                Assert.That(Status(await c.GetReceipt("ciba_1")), Is.EqualTo(401), header);
                Assert.That(c.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Bearer"));
            }
            _consent.Verify(s => s.GetReceiptAsync(It.IsAny<ReceiptCaller>(), It.IsAny<string>()), Times.Never);
        }

        [Test]
        public async Task GetReceipt_ABearerTokenCarriesSubAndClient_AClientCarriesItsAuthenticatedId()
        {
            ReceiptCaller? seen = null;
            _consent.Setup(s => s.GetReceiptAsync(It.IsAny<ReceiptCaller>(), "ciba_1")).Callback<ReceiptCaller, string>((c, _) => seen = c)
                .ReturnsAsync((ReceiptLookup.Found, "{\"version\":\"1\"}"));

            BearerFor("tok", "user-1", "chat");
            var ok = await Controller("Bearer tok").GetReceipt("ciba_1");
            Assert.That(Status(ok), Is.EqualTo(200));
            Assert.That(((ContentResult)ok).Content, Is.EqualTo("{\"version\":\"1\"}"));
            Assert.That(((ContentResult)ok).ContentType, Is.EqualTo("application/json"));
            Assert.That((seen!.Sub, seen.ClientId, seen.ClientAuthenticated), Is.EqualTo(("user-1", "chat", false)));

            _jwt.Setup(j => j.AuthenticateConfidentialClientAsync("Basic Y2hhdDpzZWNyZXQ=")).ReturnsAsync(new JwtIssuerClientConfiguration { ClientId = "chat", ClientSecret = "secret" });
            Assert.That(Status(await Controller("Basic Y2hhdDpzZWNyZXQ=").GetReceipt("ciba_1")), Is.EqualTo(200));
            Assert.That(seen!.Sub, Is.Null);
            Assert.That((seen.ClientId, seen.ClientAuthenticated), Is.EqualTo(("chat", true)));
        }

        [Test]
        public async Task GetReceipt_ATokenWithoutASubject_IsRefused()
        {
            BearerFor("tok", null, "chat");
            Assert.That(Status(await Controller("Bearer tok").GetReceipt("ciba_1")), Is.EqualTo(401));
        }

        [Test]
        public async Task GetReceipt_NotFoundAndNotReady_AreDistinct()
        {
            BearerFor("tok", "user-1", "chat");
            _consent.Setup(s => s.GetReceiptAsync(It.IsAny<ReceiptCaller>(), "ciba_a")).ReturnsAsync((ReceiptLookup.NotFound, (string?)null));
            _consent.Setup(s => s.GetReceiptAsync(It.IsAny<ReceiptCaller>(), "ciba_b")).ReturnsAsync((ReceiptLookup.NotReady, (string?)null));
            Assert.That(Status(await Controller("Bearer tok").GetReceipt("ciba_a")), Is.EqualTo(404));
            Assert.That(Status(await Controller("Bearer tok").GetReceipt("ciba_b")), Is.EqualTo(409));
        }
    }
}
