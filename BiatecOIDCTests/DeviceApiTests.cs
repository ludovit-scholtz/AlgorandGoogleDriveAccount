using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Controllers;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Moq;

namespace BiatecOIDCTests
{
    /// <summary>In-memory <see cref="IDeviceStore"/> with the same rules as the Redis one.</summary>
    internal sealed class FakeDeviceStore : IDeviceStore
    {
        public readonly Dictionary<string, DeviceRecord> Devices = new();
        private readonly HashSet<string> _jtis = new();
        private readonly Dictionary<string, int> _rate = new();

        public Task<bool> TryAddAsync(DeviceRecord device)
        {
            if (Devices.ContainsKey(device.Kid)) return Task.FromResult(false);
            Devices[device.Kid] = device;
            return Task.FromResult(true);
        }

        public Task<DeviceRecord?> GetByKidAsync(string kid) => Task.FromResult(Devices.GetValueOrDefault(kid));

        public Task<DeviceRecord?> GetByTokenHashAsync(string tokenHash)
            => Task.FromResult(Devices.Values.FirstOrDefault(d => d.TokenHash == tokenHash));

        public Task<int> CountForUserAsync(string sub) => Task.FromResult(Devices.Values.Count(d => d.Sub == sub));

        public Task<IReadOnlyList<DeviceRecord>> ListForUserAsync(string sub)
            => Task.FromResult<IReadOnlyList<DeviceRecord>>(Devices.Values.Where(d => d.Sub == sub).ToList());

        public Task SetPushAsync(string kid, string provider, string token)
        {
            foreach (var other in Devices.Values.Where(d => d.PushToken == token && d.PushProvider == provider))
            {
                other.PushToken = null;
                other.PushProvider = null;
            }
            Devices[kid].PushProvider = provider;
            Devices[kid].PushToken = token;
            return Task.CompletedTask;
        }

        public Task<bool> TryRememberProofAsync(string kid, string jti, TimeSpan ttl) => Task.FromResult(_jtis.Add(kid + ":" + jti));

        public Task<int> CountPushRegistrationAsync(string kid)
        {
            _rate[kid] = _rate.GetValueOrDefault(kid) + 1;
            return Task.FromResult(_rate[kid]);
        }
    }

    internal sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FixedTimeProvider(DateTimeOffset now) => _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
    }

    [TestFixture]
    public class DeviceApiTests
    {
        private const string Issuer = "https://oidc.test";
        private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

        private FakeDeviceStore _store = null!;
        private Mock<IJwtIssuerService> _jwt = null!;
        private FixedTimeProvider _time = null!;
        private ECDsa _deviceKey = null!;

        [SetUp]
        public void SetUp()
        {
            _store = new FakeDeviceStore();
            _time = new FixedTimeProvider(Now);
            _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _jwt = new Mock<IJwtIssuerService>();
            _jwt.Setup(j => j.GetIssuer(It.IsAny<HttpRequest>())).Returns(Issuer);
            _jwt.Setup(j => j.ValidateBearerAccessToken("user-token"))
                .Returns((true, new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(JwtRegisteredClaimNames.Sub, "user-1") })), null, null));
            _jwt.Setup(j => j.ValidateBearerAccessToken("bad")).Returns((false, null, null, "invalid"));
        }

        [TearDown]
        public void TearDown() => _deviceKey.Dispose();

        private DevicesController Controller(string? authorization, string method, string path, string? proof = null, DevicesConfiguration? config = null)
        {
            var options = Options.Create(config ?? new DevicesConfiguration());
            var c = new DevicesController(_store, _jwt.Object, options, _time, new DeviceAuthenticator(_store, _jwt.Object, options, _time));
            var http = new DefaultHttpContext();
            http.Request.Method = method;
            http.Request.Path = path;
            if (authorization is not null) http.Request.Headers.Authorization = authorization;
            if (proof is not null) http.Request.Headers[DevicesController.ProofHeader] = proof;
            c.ControllerContext = new ControllerContext { HttpContext = http };
            return c;
        }

        private DeviceJwk Jwk()
        {
            var p = _deviceKey.ExportParameters(false);
            return new DeviceJwk { Kty = "EC", Crv = "P-256", X = Base64UrlEncoder.Encode(p.Q.X!), Y = Base64UrlEncoder.Encode(p.Q.Y!) };
        }

        private string Proof(string kid, string method, string url, string token, long? iat = null, string? jti = null, string? typ = null,
            string? ath = null, ECDsa? signWith = null)
        {
            var header = JsonSerializer.Serialize(new { alg = "ES256", typ = typ ?? DeviceProofVerifier.ProofType, kid });
            var claims = JsonSerializer.Serialize(new
            {
                htm = method,
                htu = url,
                iat = iat ?? Now.ToUnixTimeSeconds(),
                jti = jti ?? Guid.NewGuid().ToString("N"),
                ath = ath ?? Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(token))),
            });
            var input = Base64UrlEncoder.Encode(header) + "." + Base64UrlEncoder.Encode(claims);
            var sig = (signWith ?? _deviceKey).SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return input + "." + Base64UrlEncoder.Encode(sig);
        }

        private async Task<(string Kid, string Token)> Enroll(string kid = "device-0001", DevicePush? push = null)
        {
            var c = Controller("Bearer user-token", "POST", "/devices");
            var r = await c.Enroll(new EnrollDeviceRequest { Kid = kid, PublicKey = Jwk(), Name = "Pixel", Push = push });
            var created = (ObjectResult)r;
            Assert.That(created.StatusCode, Is.EqualTo(201));
            return (kid, ((EnrollDeviceResponse)created.Value!).DeviceToken);
        }

        // ---- enrollment ----

        [Test]
        public async Task Enroll_ReturnsOnceShownToken_AndInactiveDevice_AndStoresOnlyItsHash()
        {
            var (kid, token) = await Enroll();
            var d = _store.Devices[kid];
            Assert.That(d.Sub, Is.EqualTo("user-1"));
            Assert.That(d.TokenHash, Is.EqualTo(DeviceStore.Hash(token)).And.Not.EqualTo(token));
            Assert.That(d.ActiveAt, Is.EqualTo(Now.ToUnixTimeSeconds() + 86400));
        }

        [Test]
        public async Task Enroll_WithoutValidUserToken_Is401()
        {
            foreach (var auth in new string?[] { null, "Bearer bad", "Device x" })
            {
                var r = await Controller(auth, "POST", "/devices").Enroll(new EnrollDeviceRequest { Kid = "device-0001", PublicKey = Jwk() });
                Assert.That(r, Is.TypeOf<UnauthorizedResult>(), auth);
            }
        }

        [TestCase("short")]
        [TestCase("has space in it")]
        [TestCase(null)]
        public async Task Enroll_RejectsABadKid(string? kid)
        {
            var r = await Controller("Bearer user-token", "POST", "/devices").Enroll(new EnrollDeviceRequest { Kid = kid, PublicKey = Jwk() });
            Assert.That(r, Is.TypeOf<BadRequestObjectResult>());
        }

        [Test]
        public async Task Enroll_RejectsAKeyThatIsNotAP256Point_AndADuplicateKid_AndTooManyDevices()
        {
            var bad = Jwk();
            bad.Y = bad.X;
            var r = await Controller("Bearer user-token", "POST", "/devices").Enroll(new EnrollDeviceRequest { Kid = "device-0001", PublicKey = bad });
            Assert.That(r, Is.TypeOf<BadRequestObjectResult>());

            await Enroll("device-0002");
            var dup = await Controller("Bearer user-token", "POST", "/devices").Enroll(new EnrollDeviceRequest { Kid = "device-0002", PublicKey = Jwk() });
            Assert.That(dup, Is.TypeOf<ConflictObjectResult>());

            var cfg = new DevicesConfiguration { MaxDevicesPerUser = 1 };
            var many = await Controller("Bearer user-token", "POST", "/devices", config: cfg).Enroll(new EnrollDeviceRequest { Kid = "device-0003", PublicKey = Jwk() });
            Assert.That(many, Is.TypeOf<ConflictObjectResult>());
        }

        [Test]
        public async Task Enroll_ReturnsAnAttestation_OnlyWhenAKeyIsConfigured()
        {
            using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var svc = new ConsentAttestationService(Options.Create(new ConsentConfiguration { AttestationSigningKeyPem = signing.ExportPkcs8PrivateKeyPem() }));
            var options = Options.Create(new DevicesConfiguration());
            var c = new DevicesController(_store, _jwt.Object, options, _time, new DeviceAuthenticator(_store, _jwt.Object, options, _time), svc);
            var http = new DefaultHttpContext();
            http.Request.Headers.Authorization = "Bearer user-token";
            c.ControllerContext = new ControllerContext { HttpContext = http };
            var r = (ObjectResult)await c.Enroll(new EnrollDeviceRequest { Kid = "device-0001", PublicKey = Jwk() });
            var attestation = ((EnrollDeviceResponse)r.Value!).Attestation;
            Assert.That(attestation, Is.Not.Null.And.Matches(@"^[\w-]+\.[\w-]+\.[\w-]+$"));

            await Enroll("device-0002");
            Assert.That(_store.Devices.ContainsKey("device-0002"));
        }

        // ---- the proof ----

        private static (int? Status, string? Challenge) Outcome(IActionResult r, DevicesController c)
            => ((r as ObjectResult)?.StatusCode ?? (r as StatusCodeResult)?.StatusCode, c.Response.Headers.WWWAuthenticate.ToString());

        private async Task<(int? Status, string Challenge)> PushCall(string kid, string token, Func<string, string> proofFor, string? authorization = null)
        {
            var c = Controller(authorization ?? "Device " + token, "PUT", "/devices/push", proofFor(kid));
            var r = await c.PutPush(new DevicePush { Provider = "fcm", Token = "installation-id-1" });
            var (s, ch) = Outcome(r, c);
            return (s ?? (r is OkObjectResult ? 200 : null), ch ?? "");
        }

        [Test]
        public async Task ValidProof_IsAccepted()
        {
            var (kid, token) = await Enroll();
            var (status, _) = await PushCall(kid, token, k => Proof(k, "PUT", Issuer + "/devices/push", token));
            Assert.That(status, Is.EqualTo(200));
            Assert.That(_store.Devices[kid].PushToken, Is.EqualTo("installation-id-1"));
        }

        [Test]
        public async Task TheBearerAlone_IsRefused_WithInvalidProof()
        {
            var (kid, token) = await Enroll();
            var (status, challenge) = await PushCall(kid, token, _ => "");
            Assert.That(status, Is.EqualTo(401));
            Assert.That(challenge, Is.EqualTo("Device error=\"invalid_proof\""));
            Assert.That(_store.Devices[kid].PushToken, Is.Null);
        }

        [Test]
        public async Task UnknownOrMissingToken_IsInvalidToken()
        {
            var (kid, token) = await Enroll();
            var (s1, c1) = await PushCall(kid, "nope", k => Proof(k, "PUT", Issuer + "/devices/push", "nope"));
            Assert.That((s1, c1), Is.EqualTo((401, "Device error=\"invalid_token\"")));
            var c = Controller("Bearer " + token, "PUT", "/devices/push");
            var r = await c.PutPush(new DevicePush { Provider = "fcm", Token = "x" });
            Assert.That(r, Is.TypeOf<UnauthorizedObjectResult>());
            Assert.That(c.Response.Headers.WWWAuthenticate.ToString(), Is.EqualTo("Device error=\"invalid_token\""));
        }

        [Test]
        public async Task ProofSignedByAnotherKey_IsRefused()
        {
            var (kid, token) = await Enroll();
            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var (status, ch) = await PushCall(kid, token, k => Proof(k, "PUT", Issuer + "/devices/push", token, signWith: other));
            Assert.That((status, ch), Is.EqualTo((401, "Device error=\"invalid_proof\"")));
        }

        [Test]
        public async Task ProofForAnotherMethodUrlTokenOrKid_IsRefused()
        {
            var (kid, token) = await Enroll();
            var cases = new Func<string, string>[]
            {
                k => Proof(k, "GET", Issuer + "/devices/push", token),
                k => Proof(k, "PUT", Issuer + "/devices/other", token),
                k => Proof(k, "PUT", "https://evil.test/devices/push", token),
                k => Proof(k, "PUT", Issuer + "/devices/push", "another-token"),
                k => Proof("someone-else-1", "PUT", Issuer + "/devices/push", token),
                k => Proof(k, "PUT", Issuer + "/devices/push", token, typ: "JWT"),
            };
            foreach (var proofFor in cases)
            {
                var (status, ch) = await PushCall(kid, token, proofFor);
                Assert.That((status, ch), Is.EqualTo((401, "Device error=\"invalid_proof\"")));
            }
        }

        [Test]
        public async Task StaleOrFutureProof_IsRefused_AndEdgeOfTheWindowIsAccepted()
        {
            var (kid, token) = await Enroll();
            var now = Now.ToUnixTimeSeconds();
            foreach (var iat in new[] { now - 61, now + 61 })
            {
                var (status, _) = await PushCall(kid, token, k => Proof(k, "PUT", Issuer + "/devices/push", token, iat: iat));
                Assert.That(status, Is.EqualTo(401), "iat " + (iat - now));
            }
            foreach (var iat in new[] { now - 60, now + 60 })
            {
                var (status, _) = await PushCall(kid, token, k => Proof(k, "PUT", Issuer + "/devices/push", token, iat: iat));
                Assert.That(status, Is.EqualTo(200), "iat " + (iat - now));
            }
        }

        [Test]
        public async Task AReplayedProof_IsRefused_AndAShortJtiToo()
        {
            var (kid, token) = await Enroll();
            var proof = Proof(kid, "PUT", Issuer + "/devices/push", token, jti: "0123456789abcdef");
            Assert.That((await PushCall(kid, token, _ => proof)).Status, Is.EqualTo(200));
            Assert.That((await PushCall(kid, token, _ => proof)).Status, Is.EqualTo(401));

            var (shortStatus, _) = await PushCall(kid, token, k => Proof(k, "PUT", Issuer + "/devices/push", token, jti: "short"));
            Assert.That(shortStatus, Is.EqualTo(401));
        }

        [Test]
        public async Task Garbage_InTheProofHeader_IsRefusedNotAnError()
        {
            var (kid, token) = await Enroll();
            foreach (var junk in new[] { "a.b.c", "x", "....", "e30.e30.e30", Base64UrlEncoder.Encode("{}") + "." + Base64UrlEncoder.Encode("[]") + "." + Base64UrlEncoder.Encode(new byte[64]) })
            {
                var (status, _) = await PushCall(kid, token, _ => junk);
                Assert.That(status, Is.EqualTo(401), junk);
            }
        }

        // ---- push registration ----

        [Test]
        public async Task PushRegistration_IsLimitedPerDevice()
        {
            var (kid, token) = await Enroll();
            for (var i = 0; i < 6; i++)
            {
                Assert.That((await PushCall(kid, token, k => Proof(k, "PUT", Issuer + "/devices/push", token))).Status, Is.EqualTo(200), "call " + i);
            }
            var c = Controller("Device " + token, "PUT", "/devices/push", Proof(kid, "PUT", Issuer + "/devices/push", token));
            var r = (ObjectResult)await c.PutPush(new DevicePush { Provider = "fcm", Token = "installation-id-1" });
            Assert.That(r.StatusCode, Is.EqualTo(429));
            Assert.That(c.Response.Headers.RetryAfter.ToString(), Is.EqualTo("60"));
        }

        [Test]
        public async Task APushToken_BelongsToOneDevice()
        {
            var (kidA, tokenA) = await Enroll("device-000A", new DevicePush { Provider = "fcm", Token = "installation-id-1" });
            var (kidB, tokenB) = await Enroll("device-000B");
            Assert.That((await PushCall(kidB, tokenB, k => Proof(k, "PUT", Issuer + "/devices/push", tokenB))).Status, Is.EqualTo(200));
            Assert.That(_store.Devices[kidB].PushToken, Is.EqualTo("installation-id-1"));
            Assert.That(_store.Devices[kidA].PushToken, Is.Null);
            _ = tokenA;
        }

        [TestCase("web", "x")]
        [TestCase("fcm", "")]
        [TestCase(null, "x")]
        public async Task PushRegistration_RejectsBadBodies(string? provider, string token)
        {
            var (kid, deviceToken) = await Enroll();
            var c = Controller("Device " + deviceToken, "PUT", "/devices/push", Proof(kid, "PUT", Issuer + "/devices/push", deviceToken));
            Assert.That(await c.PutPush(new DevicePush { Provider = provider, Token = token }), Is.TypeOf<BadRequestObjectResult>());
        }
    }
}
