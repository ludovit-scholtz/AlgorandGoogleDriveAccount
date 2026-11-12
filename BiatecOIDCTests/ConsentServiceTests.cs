using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BiatecOIDCTests
{
    /// <summary>In-memory <see cref="IConsentStore"/> with the same rules as the Redis one (which has its own tests against a real Redis).</summary>
    internal sealed class FakeConsentStore : IConsentStore
    {
        public readonly Dictionary<string, ConsentRequestRecord> Records = new();
        private readonly Dictionary<string, SortedSet<string>> _open = new();
        private readonly Dictionary<string, List<long>> _rate = new();
        public int ConflictsToInject;

        private static ConsentRequestRecord Copy(ConsentRequestRecord r) => JsonSerializer.Deserialize<ConsentRequestRecord>(JsonSerializer.Serialize(r))!;

        public Task<OpenOutcome> TryOpenAsync(ConsentRequestRecord record, ConsentLimits limits, DateTimeOffset now)
        {
            var n = now.ToUnixTimeSeconds();
            foreach (var set in _open.Values)
            {
                set.RemoveWhere(id => Records.TryGetValue(id, out var r) && r.Expiry <= n);
            }
            var user = Bucket("u:" + record.Sub);
            var origin = Bucket("o:" + record.ClientId + "|" + record.Sub + "|" + record.Origin);
            var rateKey = record.ClientId + "|" + record.Sub;
            var recent = _rate.TryGetValue(rateKey, out var list) ? list : _rate[rateKey] = new List<long>();
            recent.RemoveAll(t => t <= n - 3600);
            if (user.Count >= limits.MaxOpenPerUser) return Task.FromResult(OpenOutcome.UserCap);
            if (origin.Count >= limits.MaxPendingPerClientOrigin) return Task.FromResult(OpenOutcome.OriginCap);
            if (recent.Count >= limits.MaxPerClientUserHour) return Task.FromResult(OpenOutcome.RateLimited);
            if (Records.ContainsKey(record.AuthReqId)) return Task.FromResult(OpenOutcome.IdCollision);
            Records[record.AuthReqId] = Copy(record);
            user.Add(record.AuthReqId);
            origin.Add(record.AuthReqId);
            recent.Add(n);
            return Task.FromResult(OpenOutcome.Opened);
        }

        private SortedSet<string> Bucket(string key)
            => _open.TryGetValue(key, out var s) ? s : _open[key] = new SortedSet<string>();

        public Task<ConsentRequestRecord?> GetAsync(string authReqId) => Task.FromResult(Records.TryGetValue(authReqId, out var r) ? Copy(r) : null);

        public Task<bool> TryUpdateAsync(ConsentRequestRecord expected, ConsentRequestRecord updated)
        {
            if (ConflictsToInject > 0)
            {
                ConflictsToInject--;
                Records[expected.AuthReqId].Version++; // someone else got in first
                return Task.FromResult(false);
            }
            if (!Records.TryGetValue(expected.AuthReqId, out var cur) || cur.Version != expected.Version) return Task.FromResult(false);
            updated.Version = expected.Version + 1;
            Records[expected.AuthReqId] = Copy(updated);
            if (updated.IsDecided && !expected.IsDecided)
            {
                Bucket("u:" + expected.Sub).Remove(expected.AuthReqId);
                Bucket("o:" + expected.ClientId + "|" + expected.Sub + "|" + expected.Origin).Remove(expected.AuthReqId);
            }
            return Task.FromResult(true);
        }

        public Task<IReadOnlyList<string>> ListOpenIdsAsync(string sub, DateTimeOffset now)
            => Task.FromResult<IReadOnlyList<string>>(Bucket("u:" + sub).Where(id => Records[id].Expiry > now.ToUnixTimeSeconds()).ToList());

        public Task ReleaseAsync(ConsentRequestRecord record)
        {
            Bucket("u:" + record.Sub).Remove(record.AuthReqId);
            Bucket("o:" + record.ClientId + "|" + record.Sub + "|" + record.Origin).Remove(record.AuthReqId);
            return Task.CompletedTask;
        }
    }

    internal sealed class MovableTimeProvider : TimeProvider
    {
        public DateTimeOffset Now;
        public MovableTimeProvider(DateTimeOffset now) => Now = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [TestFixture]
    public class ConsentServiceTests
    {
        private const string Issuer = "https://oidc.test";
        private static readonly DateTimeOffset Start = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);

        private FakeConsentStore _store = null!;
        private FakeDeviceStore _devices = null!;
        private ConsentAttestationService _attestation = null!;
        private MovableTimeProvider _time = null!;
        private ConsentService _svc = null!;
        private ECDsa _deviceKey = null!;
        private DeviceRecord _device = null!;

        [SetUp]
        public void SetUp()
        {
            _store = new FakeConsentStore();
            _devices = new FakeDeviceStore();
            _time = new MovableTimeProvider(Start);
            using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _attestation = new ConsentAttestationService(Options.Create(new ConsentConfiguration { AttestationSigningKeyPem = signing.ExportPkcs8PrivateKeyPem() }));
            _deviceKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = _deviceKey.ExportParameters(false);
            _device = new DeviceRecord
            {
                Kid = "device-0001", Sub = "user-1", KeyX = Base64UrlEncoder.Encode(p.Q.X!), KeyY = Base64UrlEncoder.Encode(p.Q.Y!),
                TokenHash = "h", ActiveAt = Start.ToUnixTimeSeconds() - 3600,
            };
            _devices.Devices[_device.Kid] = _device;
            _svc = Service(new ConsentConfiguration { AttestationSigningKeyPem = null });
        }

        [TearDown]
        public void TearDown()
        {
            _deviceKey.Dispose();
            _attestation.Dispose();
        }

        private const string CodeKey = "test-binding-key";

        private ConsentService Service(ConsentConfiguration config)
        {
            config.BindingCodeKey ??= CodeKey;
            return new(_store, _devices, _attestation, Options.Create(config), _time, NullLogger<ConsentService>.Instance);
        }

        private static JsonElement Details(string groupHash = "BJPM", string amount = "5000.000000")
            => JsonDocument.Parse(new JsonArray(new JsonObject
            {
                ["type"] = "biatec_transaction", ["network"] = "algorand-mainnet", ["group_hash"] = groupHash, ["amount"] = amount,
                ["sender"] = "SENDER", ["receiver"] = "RECEIVER",
            }).ToJsonString()).RootElement;

        private OpenConsentCommand Command(Action<OpenConsentCommand>? tweak = null)
        {
            var c = new OpenConsentCommand
            {
                Sub = "user-1", ClientId = "chat", ClientEntityId = "https://chat.test", ClientVerified = true, ClientDisplayName = "Biatec Chat",
                ClientDomain = "chat.test", Entry = "step-up", Origin = "https://app.test", Scope = "sign", AuthorizationDetails = Details(),
                Resource = "https://mcp.biatec.io/tools",
                TokenClaims = new List<ClaimSnapshot> { new() { Type = "sub", Value = "user-1" }, new() { Type = "sign", Value = "true" } },
            };
            tweak?.Invoke(c);
            return c;
        }

        private async Task<OpenConsentResult> Open(Action<OpenConsentCommand>? tweak = null)
        {
            var r = await _svc.OpenAsync(Command(tweak));
            Assert.That(r.Failure, Is.EqualTo(OpenConsentFailure.None), r.Detail);
            return r;
        }

        /// <summary>A receipt the way the authenticator builds it: signed object from the fetched request, device signature, attestation.</summary>
        private JsonElement Receipt(string authReqId, string decision, string code, Action<JsonObject>? tweakSigned = null, DeviceRecord? as_ = null, string? attestation = null)
        {
            var record = _store.Records[authReqId];
            var signed = new JsonObject
            {
                ["authorization_details"] = JsonNode.Parse(record.AuthorizationDetailsJson),
                ["auth_req_id"] = record.AuthReqId,
                ["client_entity_id"] = record.ClientEntityId,
                ["binding_code"] = code,
                ["decision"] = decision,
                ["expiry"] = record.Expiry,
                ["sub"] = record.Sub,
                ["binding_message"] = record.BindingMessage,
            };
            tweakSigned?.Invoke(signed);
            var d = as_ ?? _device;
            var sig = _deviceKey.SignData(JsonCanonicalizer.CanonicalizeToUtf8(JsonDocument.Parse(signed.ToJsonString()).RootElement),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var envelope = new JsonObject
            {
                ["version"] = "1", ["signed"] = signed, ["alg"] = "ES256", ["signature"] = Base64UrlEncoder.Encode(sig), ["issued_at"] = Start.ToUnixTimeSeconds(),
                ["device"] = new JsonObject
                {
                    ["kid"] = d.Kid,
                    ["public_key"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = d.KeyX, ["y"] = d.KeyY },
                    ["attestation"] = attestation ?? _attestation.Issue(Issuer, d.Sub, d.Kid, d.KeyX, d.KeyY, Start),
                },
            };
            return JsonDocument.Parse(envelope.ToJsonString()).RootElement;
        }

        private static string WrongCode(string code) => code == "000000" ? "000001" : "000000";

        // ---- opening ----

        [Test]
        public async Task Open_ReturnsIdIntervalExpiryAndASixDigitCode_AndStoresOnlyAHashOfTheCode()
        {
            var r = await Open();
            Assert.That(r.AuthReqId, Does.Match("^ciba_[0-9a-f]{32}$"));
            Assert.That(r.BindingCode, Does.Match("^[0-9]{6}$"));
            Assert.That(r.ExpiresIn, Is.EqualTo(300));
            Assert.That(r.Interval, Is.EqualTo(2));
            var rec = _store.Records[r.AuthReqId];
            Assert.That(JsonSerializer.Serialize(rec), Does.Not.Contain(r.BindingCode).Or.Contain("ciba_"), "the stored record holds no plain code");
            Assert.That(rec.BindingCodeHash, Is.EqualTo(ConsentService.HashCode(System.Text.Encoding.UTF8.GetBytes(CodeKey), r.AuthReqId, r.BindingCode)));
            Assert.That(rec.BindingCodeHash, Is.Not.EqualTo(ConsentService.HashCode(System.Text.Encoding.UTF8.GetBytes("another-key"), r.AuthReqId, r.BindingCode)), "the hash is keyed");
            Assert.That(rec.AttemptsRemaining, Is.EqualTo(3));
        }

        [Test]
        public async Task Open_CodesAreNotSequentialOrRepeating()
        {
            var codes = new HashSet<string>();
            var svc = Service(new ConsentConfiguration { MaxOpenRequestsPerUser = 1000, MaxPendingPerClientOrigin = 1000, MaxRequestsPerClientUserPerHour = 1000 });
            for (var i = 0; i < 60; i++)
            {
                codes.Add((await svc.OpenAsync(Command())).BindingCode);
            }
            Assert.That(codes.Count, Is.GreaterThan(50));
        }

        [Test]
        public async Task Open_FailsClosed_WithoutAnAttestationKey()
        {
            using var none = new ConsentAttestationService(Options.Create(new ConsentConfiguration()));
            var svc = new ConsentService(_store, _devices, none, Options.Create(new ConsentConfiguration()), _time, NullLogger<ConsentService>.Instance);
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.NotConfigured));
            Assert.That(_store.Records, Is.Empty);
        }

        [Test]
        public async Task Open_FailsClosed_WithoutAKeyForTheBindingCodeHash()
        {
            var svc = new ConsentService(_store, _devices, _attestation, Options.Create(new ConsentConfiguration()), _time, NullLogger<ConsentService>.Instance);
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.NotConfigured));
            // With only the attestation key configured the hash key is derived from it, so a deployment needs nothing extra.
            using var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var derived = new ConsentService(_store, _devices, _attestation,
                Options.Create(new ConsentConfiguration { AttestationSigningKeyPem = signing.ExportPkcs8PrivateKeyPem() }), _time, NullLogger<ConsentService>.Instance);
            Assert.That((await derived.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.None));
        }

        [Test]
        public async Task Open_NeedsAnActiveDevice_RuleEight()
        {
            _devices.Devices.Clear();
            Assert.That((await _svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.NoActiveDevice));
            _devices.Devices[_device.Kid] = _device;
            _device.ActiveAt = Start.ToUnixTimeSeconds() + 10; // still in its activation delay
            Assert.That((await _svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.NoActiveDevice));
            _time.Now = Start.AddSeconds(11);
            Assert.That((await _svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.None));
            // Another user's active device does not count.
            _devices.Devices.Clear();
            _devices.Devices["other-0001"] = new DeviceRecord { Kid = "other-0001", Sub = "user-2", ActiveAt = 0 };
            Assert.That((await _svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.NoActiveDevice));
        }

        [Test]
        public async Task Open_RefusesBadRequests_AndStoresNothing()
        {
            var cases = new Dictionary<string, Action<OpenConsentCommand>>
            {
                ["no sub"] = c => c.Sub = "",
                ["bad entry"] = c => c.Entry = "push",
                ["origin with path"] = c => c.Origin = "https://app.test/x",
                ["origin not a url"] = c => c.Origin = "app.test",
                ["origin javascript"] = c => c.Origin = "javascript:alert(1)",
                ["origin with credentials"] = c => c.Origin = "https://u:p@app.test",
                ["origin with fragment"] = c => c.Origin = "https://app.test/#x",
                ["origin with query"] = c => c.Origin = "https://app.test?x=1",
                ["origin with a space"] = c => c.Origin = "https://app .test",
                ["origin with a unicode host"] = c => c.Origin = "https://b\u00fccher.test",
                ["origin with a control character"] = c => c.Origin = "https://app.test\u0000",
                ["message with a control character"] = c => c.BindingMessage = "line\nbreak",
                ["message with a bidi override"] = c => c.BindingMessage = "pay \u202eevil",
                ["display name too long"] = c => c.ClientDisplayName = new string('x', 101),
                ["scope too long"] = c => c.Scope = new string('x', 201),
                ["client id too long"] = c => c.ClientId = new string('x', 201),
                ["empty origin"] = c => c.Origin = "",
                ["long message"] = c => c.BindingMessage = new string('x', 141),
                ["details not an array"] = c => c.AuthorizationDetails = JsonDocument.Parse("{}").RootElement,
                ["two details"] = c => c.AuthorizationDetails = JsonDocument.Parse("[{\"type\":\"biatec_transaction\",\"group_hash\":\"a\"},{\"type\":\"biatec_transaction\",\"group_hash\":\"b\"}]").RootElement,
                ["unknown type"] = c => c.AuthorizationDetails = JsonDocument.Parse("[{\"type\":\"payment\",\"group_hash\":\"a\"}]").RootElement,
                ["no hash"] = c => c.AuthorizationDetails = JsonDocument.Parse("[{\"type\":\"biatec_transaction\"}]").RootElement,
                ["amount as a big integer"] = c => c.AuthorizationDetails = JsonDocument.Parse("[{\"type\":\"biatec_transaction\",\"group_hash\":\"a\",\"amount\":9007199254740993}]").RootElement,
                ["duplicate member"] = c => c.AuthorizationDetails = JsonDocument.Parse("[{\"type\":\"biatec_transaction\",\"group_hash\":\"a\",\"group_hash\":\"b\"}]").RootElement,
            };
            foreach (var (name, tweak) in cases)
            {
                var r = await _svc.OpenAsync(Command(tweak));
                Assert.That(r.Failure, Is.EqualTo(OpenConsentFailure.InvalidRequest), name);
            }
            Assert.That(_store.Records, Is.Empty);
        }

        [TestCase("https://app.test", "https://app.test")]
        [TestCase("https://app.test/", "https://app.test")]
        [TestCase("HTTPS://APP.TEST", "https://app.test")]
        [TestCase("https://app.test:443", "https://app.test")]
        [TestCase("http://app.test:80/", "http://app.test")]
        [TestCase("https://app.test:8443", "https://app.test:8443")]
        [TestCase("https://xn--bcher-kva.test", "https://xn--bcher-kva.test")]
        public void Origins_HaveOneSpelling(string input, string expected)
            => Assert.That(ConsentService.NormalizeOrigin(input), Is.EqualTo(expected));

        [Test]
        public async Task Open_TheOriginCapCannotBeSidesteppedByASpelling()
        {
            var svc = Service(new ConsentConfiguration { MaxPendingPerClientOrigin = 2, MaxOpenRequestsPerUser = 100 });
            Assert.That((await svc.OpenAsync(Command(c => c.Origin = "https://app.test"))).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await svc.OpenAsync(Command(c => c.Origin = "HTTPS://APP.TEST/"))).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await svc.OpenAsync(Command(c => c.Origin = "https://app.test:443"))).Failure, Is.EqualTo(OpenConsentFailure.OriginCap));
            Assert.That(_store.Records.Values.Select(r => r.Origin).Distinct(), Is.EqualTo(new[] { "https://app.test" }));
        }

        [Test]
        public async Task Open_AnUnverifiedClientCannotPassItselfOffAsAnotherEntity()
        {
            var r = await Open(c => { c.ClientVerified = false; c.ClientEntityId = "https://chat.biatec.io"; c.ClientId = "evil-client"; });
            Assert.That(_store.Records[r.AuthReqId].ClientEntityId, Is.EqualTo("evil-client"));
            var view = (await _svc.ListOpenForDeviceAsync(_device)).Single();
            Assert.That(view["client"]!["entity_id"]!.GetValue<string>(), Is.EqualTo("evil-client"));
            Assert.That(view.ToJsonString(), Does.Not.Contain("chat.biatec.io"));
        }

        [Test]
        public async Task Open_StoresTheCanonicalDetails_AndTheBoundHashOfTheDecodedGroup()
        {
            var r = await Open(c => c.AuthorizationDetails = Details(groupHash: "HASH-1"));
            var rec = _store.Records[r.AuthReqId];
            Assert.That(rec.BoundHash, Is.EqualTo("HASH-1"));
            Assert.That(rec.AuthorizationDetailsJson, Is.EqualTo(JsonCanonicalizer.Canonicalize(rec.AuthorizationDetailsJson)));
        }

        [Test]
        public async Task Open_AnUnverifiedClient_HasNoFreeTextChannelAndNoIdentityFields()
        {
            var r = await Open(c => { c.ClientVerified = false; c.BindingMessage = "Send 1000 to my friend"; c.ClientDisplayName = "Biatec Bank"; c.ClientDomain = "bank.test"; });
            var rec = _store.Records[r.AuthReqId];
            Assert.That(rec.BindingMessage, Is.EqualTo(""));
            Assert.That(rec.ClientDisplayName, Is.Null);
            Assert.That(rec.ClientDomain, Is.Null);
            var view = (await _svc.ListOpenForDeviceAsync(_device)).Single();
            var client = view["client"]!.AsObject();
            Assert.That(client["verified"]!.GetValue<bool>(), Is.False);
            Assert.That(client["fingerprint"]!.GetValue<string>(), Does.Match("^[0-9A-F]{4}-[0-9A-F]{4}$"));
            Assert.That(client.ContainsKey("display_name"), Is.False);
            Assert.That(client.ContainsKey("domain"), Is.False);
            Assert.That(view.ToJsonString(), Does.Not.Contain("Biatec Bank").And.Not.Contain("friend"));
        }

        [Test]
        public async Task Open_AVerifiedClient_GetsItsMessageShownAndSigned()
        {
            var r = await Open(c => c.BindingMessage = "  Invoice 2026-0042 ");
            Assert.That(_store.Records[r.AuthReqId].BindingMessage, Is.EqualTo("Invoice 2026-0042"));
            var client = (await _svc.ListOpenForDeviceAsync(_device)).Single()["client"]!.AsObject();
            Assert.That(client["display_name"]!.GetValue<string>(), Is.EqualTo("Biatec Chat"));
        }

        [Test]
        public async Task Open_ValidityIsCappedByRequestedExpiryAndMaxConsentSeconds()
        {
            _svc = Service(new ConsentConfiguration { MaxPendingPerClientOrigin = 100 });
            Assert.That((await Open(c => c.RequestedExpirySeconds = 120)).ExpiresIn, Is.EqualTo(120));
            Assert.That((await Open(c => c.RequestedExpirySeconds = 9999)).ExpiresIn, Is.EqualTo(300), "never above the default and maximum");
            Assert.That((await Open(c => c.MaxConsentSeconds = 45)).ExpiresIn, Is.EqualTo(45));
            Assert.That((await Open(c => c.RequestedExpirySeconds = 0)).ExpiresIn, Is.EqualTo(1));
        }

        [Test]
        public async Task Open_Caps_UserOriginAndRate()
        {
            var svc = Service(new ConsentConfiguration { MaxOpenRequestsPerUser = 4, MaxPendingPerClientOrigin = 2, MaxRequestsPerClientUserPerHour = 100 });
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.OriginCap));
            Assert.That((await svc.OpenAsync(Command(c => c.Origin = "https://b.test"))).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await svc.OpenAsync(Command(c => c.Origin = "https://c.test"))).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await svc.OpenAsync(Command(c => c.Origin = "https://d.test"))).Failure, Is.EqualTo(OpenConsentFailure.UserCap));

            var limited = Service(new ConsentConfiguration { MaxRequestsPerClientUserPerHour = 1 });
            Assert.That((await limited.OpenAsync(Command(c => c.ClientId = "rate-client"))).Failure, Is.EqualTo(OpenConsentFailure.None));
            Assert.That((await limited.OpenAsync(Command(c => c.ClientId = "rate-client"))).Failure, Is.EqualTo(OpenConsentFailure.RateLimited));
            Assert.That((await limited.OpenAsync(Command(c => c.ClientId = "another-client"))).Failure, Is.EqualTo(OpenConsentFailure.None), "the rate is per client");
        }

        // ---- what the device sees ----

        [Test]
        public async Task DeviceView_NeverContainsTheBindingCode_AndMatchesTheConsentRequestSchema()
        {
            var r = await Open();
            var views = await _svc.ListOpenForDeviceAsync(_device);
            var single = await _svc.GetForDeviceAsync(_device, r.AuthReqId);
            foreach (var view in new[] { views.Single(), single! })
            {
                var text = view.ToJsonString();
                Assert.That(text, Does.Not.Contain(r.BindingCode));
                Assert.That(text, Does.Not.Contain("binding_code").And.Not.Contain("BindingCodeHash").And.Not.Contain(_store.Records[r.AuthReqId].BindingCodeHash));
                Assert.That(view.Select(p => p.Key).OrderBy(k => k), Is.EqualTo(new[]
                    { "auth_req_id", "authorization_details", "binding_message", "bound_hash", "client", "entry", "expiry", "origin", "requested_expiry", "sub" }));
            }
        }

        [Test]
        public async Task DeviceView_IsOnlyForTheDevicesOwnUser_AndOnlyWhileOpen()
        {
            var r = await Open();
            var stranger = new DeviceRecord { Kid = "device-0009", Sub = "user-2", ActiveAt = 0 };
            Assert.That(await _svc.GetForDeviceAsync(stranger, r.AuthReqId), Is.Null);
            Assert.That(await _svc.ListOpenForDeviceAsync(stranger), Is.Empty);
            Assert.That(await _svc.GetForDeviceAsync(_device, "ciba_unknown"), Is.Null);

            _time.Now = Start.AddSeconds(301);
            Assert.That(await _svc.GetForDeviceAsync(_device, r.AuthReqId), Is.Null, "expired");
            Assert.That(await _svc.ListOpenForDeviceAsync(_device), Is.Empty);
        }

        [Test]
        public async Task DeviceView_IsEmptyForADeviceInItsActivationDelay_EvenAtTheServiceLevel()
        {
            var r = await Open();
            _device.ActiveAt = Start.ToUnixTimeSeconds() + 60;
            Assert.That(await _svc.ListOpenForDeviceAsync(_device), Is.Empty);
            Assert.That(await _svc.GetForDeviceAsync(_device, r.AuthReqId), Is.Null);
        }

        // ---- receipts ----

        [Test]
        public async Task Approve_WithTheRightCode_IsRecordedOnce_AndTheStoredReceiptIsRebuiltFromTheVerifiedMembers()
        {
            var open = await Open();
            var receipt = Receipt(open.AuthReqId, "approve", open.BindingCode);
            var result = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, receipt, Issuer);
            Assert.That(result.Outcome, Is.EqualTo(ReceiptOutcome.Approved));
            var rec = _store.Records[open.AuthReqId];
            Assert.That(rec.Status, Is.EqualTo(ConsentStatus.Approved));
            Assert.That(JsonCanonicalizer.Canonicalize(rec.ReceiptJson!), Is.EqualTo(JsonCanonicalizer.Canonicalize(receipt)));
            Assert.That(rec.DecidedByDeviceKid, Is.EqualTo("device-0001"));
            Assert.That(await _svc.ListOpenForDeviceAsync(_device), Is.Empty, "a decided request is no longer pending");
            Assert.That(rec.ReceiptJson, Does.Not.Contain("\"bindingCodeHash\""));

            // The same receipt again, or a different decision, is refused: the consent is decided once.
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, receipt, Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.AlreadyDecided));
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "deny", ""), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.AlreadyDecided));
        }

        [Test]
        public async Task ApproveReleasesTheOpenSlot()
        {
            var svc = Service(new ConsentConfiguration { MaxPendingPerClientOrigin = 1 });
            var open = await svc.OpenAsync(Command());
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.OriginCap));
            await svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            Assert.That((await svc.OpenAsync(Command())).Failure, Is.EqualTo(OpenConsentFailure.None));
        }

        [Test]
        public async Task WrongCode_CountsDown_ThenLocks_AndOnlyCodeFailedIsAcceptedAfterwards()
        {
            var open = await Open();
            var wrong = WrongCode(open.BindingCode);

            var first = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", wrong), Issuer);
            Assert.That((first.Outcome, first.AttemptsRemaining), Is.EqualTo((ReceiptOutcome.WrongCode, 2)));
            var second = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", wrong), Issuer);
            Assert.That((second.Outcome, second.AttemptsRemaining), Is.EqualTo((ReceiptOutcome.WrongCode, 1)));
            var third = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", wrong), Issuer);
            Assert.That(third.Outcome, Is.EqualTo(ReceiptOutcome.CodeLocked));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.CodeLocked));

            // Even the right code no longer works, and neither does a deny: only the signed code_failed receipt.
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.CodeLocked));
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "deny", ""), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.CodeLocked));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.CodeLocked));

            var failed = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "code_failed", wrong), Issuer);
            Assert.That(failed.Outcome, Is.EqualTo(ReceiptOutcome.CodeFailedRecorded));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.CodeFailed));
        }

        [Test]
        public async Task ARightCodeAfterTwoWrongOnes_StillApproves()
        {
            var open = await Open();
            var wrong = WrongCode(open.BindingCode);
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", wrong), Issuer);
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", wrong), Issuer);
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.Approved));
        }

        [Test]
        public async Task CodeFailed_WithoutALock_IsRefused_SoAClientCannotForgeADenialReason()
        {
            var open = await Open();
            var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "code_failed", "000000"), Issuer);
            Assert.That((r.Outcome, r.Detail), Is.EqualTo((ReceiptOutcome.InvalidReceipt, "code_failed_not_allowed")));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.Pending));
        }

        [Test]
        public async Task Deny_IsRecorded_WithoutNeedingACode()
        {
            var open = await Open();
            var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "deny", ""), Issuer);
            Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.Denied));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.Denied));
        }

        [TestCase("12345")]
        [TestCase("1234567")]
        [TestCase("12345a")]
        [TestCase("")]
        [TestCase("١٢٣٤٥٦")] // Arabic-Indic digits: not ASCII digits
        public async Task Approve_WithAMalformedCode_IsRefusedWithoutSpendingAnAttempt(string code)
        {
            var open = await Open();
            var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", code), Issuer);
            Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.InvalidReceipt));
            Assert.That(_store.Records[open.AuthReqId].AttemptsRemaining, Is.EqualTo(3));
        }

        [Test]
        public async Task Receipt_ForAnExpiredRequest_IsGone_AndFreesTheSlot()
        {
            var open = await Open();
            var receipt = Receipt(open.AuthReqId, "approve", open.BindingCode);
            _time.Now = Start.AddSeconds(300);
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, receipt, Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.Expired));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.Pending), "an expired request is never approved");
        }

        [Test]
        public async Task Receipt_FromAnInactiveDevice_IsRefused()
        {
            var open = await Open();
            _device.ActiveAt = Start.ToUnixTimeSeconds() + 5;
            var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.DeviceInactive));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.Pending));
        }

        [Test]
        public async Task Receipt_ForAnotherUsersRequest_LooksLikeAnUnknownRequest()
        {
            var open = await Open();
            var other = new DeviceRecord { Kid = "device-0009", Sub = "user-2", KeyX = _device.KeyX, KeyY = _device.KeyY, ActiveAt = 0 };
            Assert.That((await _svc.SubmitReceiptAsync(other, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode, as_: other), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.NotFound));
            Assert.That((await _svc.SubmitReceiptAsync(_device, "ciba_nope", Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.NotFound));
        }

        [Test]
        public async Task Receipt_ThatDoesNotMatchTheStoredRequest_IsRefused_MemberForMember()
        {
            var open = await Open();
            var tampers = new Dictionary<string, Action<JsonObject>>
            {
                ["auth_req_id"] = s => s["auth_req_id"] = "ciba_00000000000000000000000000000000",
                ["client_entity_id"] = s => s["client_entity_id"] = "https://evil.test",
                ["binding_message"] = s => s["binding_message"] = "Pay the invoice",
                ["sub"] = s => s["sub"] = "user-2",
                ["expiry"] = s => s["expiry"] = _store.Records[open.AuthReqId].Expiry + 1000,
                ["amount"] = s => s["authorization_details"]![0]!["amount"] = "1.000000",
                ["group hash"] = s => s["authorization_details"]![0]!["group_hash"] = "OTHER",
                ["extra detail"] = s => s["authorization_details"]![0]!["note"] = "x",
                ["second detail"] = s => s["authorization_details"]!.AsArray().Add(JsonNode.Parse("{\"type\":\"biatec_transaction\",\"group_hash\":\"x\"}")),
                ["decision"] = s => s["decision"] = "maybe",
                ["no decision"] = s => s.Remove("decision"),
            };
            foreach (var (name, tamper) in tampers)
            {
                var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode, tamper), Issuer);
                Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.InvalidReceipt), name);
            }
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.Pending));
            Assert.That(_store.Records[open.AuthReqId].AttemptsRemaining, Is.EqualTo(3), "a refused receipt costs no attempt");
        }

        [Test]
        public async Task Receipt_SignedByAnotherKey_OrBringingItsOwnKey_IsRefused()
        {
            var open = await Open();
            using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = attacker.ExportParameters(false);
            var attackerDevice = new DeviceRecord
            {
                Kid = _device.Kid, Sub = _device.Sub, KeyX = Base64UrlEncoder.Encode(p.Q.X!), KeyY = Base64UrlEncoder.Encode(p.Q.Y!), ActiveAt = 0,
            };
            // The attacker signs with their own key and presents it (even with a validly issued attestation for it): the enrolled key is what counts.
            var envelope = JsonNode.Parse(Receipt(open.AuthReqId, "approve", open.BindingCode, as_: attackerDevice).GetRawText())!;
            var r1 = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, JsonDocument.Parse(envelope.ToJsonString()).RootElement, Issuer);
            Assert.That(r1.Outcome, Is.EqualTo(ReceiptOutcome.InvalidReceipt));
            Assert.That(r1.Detail, Is.EqualTo("device_key_mismatch"));

            // The enrolled key's identity, but a signature made with another key.
            var forged = JsonNode.Parse(Receipt(open.AuthReqId, "approve", open.BindingCode).GetRawText())!;
            var sig = attacker.SignData(new byte[] { 1 }, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            forged["signature"] = Base64UrlEncoder.Encode(sig);
            var r2 = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, JsonDocument.Parse(forged.ToJsonString()).RootElement, Issuer);
            Assert.That(r2.Detail, Is.EqualTo("bad_signature"));
            Assert.That(_store.Records[open.AuthReqId].Status, Is.EqualTo(ConsentStatus.Pending));
        }

        [Test]
        public async Task Receipt_WithABadAttestation_IsRefused()
        {
            var open = await Open();
            using var otherIssuer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            using var other = new ConsentAttestationService(Options.Create(new ConsentConfiguration { AttestationSigningKeyPem = otherIssuer.ExportPkcs8PrivateKeyPem() }));
            var foreign = other.Issue(Issuer, _device.Sub, _device.Kid, _device.KeyX, _device.KeyY, Start)!;
            var wrongSub = _attestation.Issue(Issuer, "user-2", _device.Kid, _device.KeyX, _device.KeyY, Start)!;
            var wrongIssuer = _attestation.Issue("https://elsewhere.test", _device.Sub, _device.Kid, _device.KeyX, _device.KeyY, Start)!;
            foreach (var attestation in new[] { foreign, wrongSub, wrongIssuer, "a.b.c", "" })
            {
                var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode, attestation: attestation), Issuer);
                Assert.That((r.Outcome, r.Detail), Is.EqualTo((ReceiptOutcome.InvalidReceipt, "bad_attestation")), attestation);
            }
            _time.Now = Start.AddDays(401);
            var expired = await Open();
            var late = await _svc.SubmitReceiptAsync(_device, expired.AuthReqId, Receipt(expired.AuthReqId, "approve", expired.BindingCode, attestation: _attestation.Issue(Issuer, _device.Sub, _device.Kid, _device.KeyX, _device.KeyY, Start)), Issuer);
            Assert.That(late.Detail, Is.EqualTo("bad_attestation"), "an expired attestation");
        }

        [Test]
        public async Task Receipt_Malformed_IsRefused_NeverThrows()
        {
            var open = await Open();
            foreach (var junk in new[] { "null", "[]", "\"x\"", "{}", "{\"version\":\"2\"}", "{\"version\":\"1\",\"alg\":\"ES256\",\"signed\":[],\"device\":{}}" })
            {
                var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, JsonDocument.Parse(junk).RootElement, Issuer);
                Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.InvalidReceipt), junk);
            }
        }

        [Test]
        public async Task TwoDevicesDeciding_ExactlyOneWins()
        {
            var open = await Open();
            var approve = Receipt(open.AuthReqId, "approve", open.BindingCode);
            var deny = Receipt(open.AuthReqId, "deny", "");
            var results = await Task.WhenAll(
                _svc.SubmitReceiptAsync(_device, open.AuthReqId, approve, Issuer),
                _svc.SubmitReceiptAsync(_device, open.AuthReqId, deny, Issuer));
            Assert.That(results.Count(r => r.Outcome is ReceiptOutcome.Approved or ReceiptOutcome.Denied), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Outcome == ReceiptOutcome.AlreadyDecided), Is.EqualTo(1));
        }

        [Test]
        public async Task ALostRace_IsRetried_AndGivesUpWithAConflictInsteadOfLoopingForever()
        {
            var open = await Open();
            _store.ConflictsToInject = 2;
            Assert.That((await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.Approved));

            var second = await Open();
            _store.ConflictsToInject = 50;
            Assert.That((await _svc.SubmitReceiptAsync(_device, second.AuthReqId, Receipt(second.AuthReqId, "approve", second.BindingCode), Issuer)).Outcome, Is.EqualTo(ReceiptOutcome.Conflict));
            Assert.That(_store.Records[second.AuthReqId].Status, Is.EqualTo(ConsentStatus.Pending));
        }

        [Test]
        public async Task Receipt_WithExtraOrDuplicatedMembers_IsRefused_AndNothingExtraIsEverStored()
        {
            var open = await Open();
            var good = Receipt(open.AuthReqId, "approve", open.BindingCode).GetRawText();

            string With(string find, string replace) => good.Replace(find, replace);
            var variants = new Dictionary<string, string>
            {
                ["extra top-level member"] = good[..^1] + ",\"note\":\"x\"}",
                ["duplicated top-level member"] = good[..^1] + ",\"alg\":\"ES256\"}",
                ["duplicated signature"] = good[..^1] + ",\"signature\":\"AAAA\"}",
                ["extra signed member"] = With("\"binding_message\":\"\"", "\"binding_message\":\"\",\"extra\":1"),
                ["extra device member"] = With("\"device\":{", "\"device\":{\"model\":\"x\","),
                ["extra key member"] = With("\"public_key\":{", "\"public_key\":{\"d\":\"x\","),
                ["missing issued_at"] = With("\"issued_at\":" + Start.ToUnixTimeSeconds() + ",", ""),
            };
            foreach (var (name, json) in variants)
            {
                var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, JsonDocument.Parse(json).RootElement, Issuer);
                Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.InvalidReceipt), name);
            }
            Assert.That(_store.Records[open.AuthReqId].ReceiptJson, Is.Null);
            Assert.That(_store.Records[open.AuthReqId].AttemptsRemaining, Is.EqualTo(3));
        }

        [Test]
        public async Task APendingRecord_NeverClaimsADecidingDevice()
        {
            var open = await Open();
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", WrongCode(open.BindingCode)), Issuer);
            Assert.That(_store.Records[open.AuthReqId].DecidedByDeviceKid, Is.Null);
            Assert.That(_store.Records[open.AuthReqId].DecidedAt, Is.Null);
        }

        [Test]
        public async Task ALostRaceThatGivesUp_ReportsAConflict_NotADecision()
        {
            var open = await Open();
            _store.ConflictsToInject = 50;
            var r = await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            Assert.That(r.Outcome, Is.EqualTo(ReceiptOutcome.Conflict));
            Assert.That(r.Outcome, Is.Not.EqualTo(ReceiptOutcome.AlreadyDecided));
        }

        // ---- polling (issue 15: slow_down) ----

        [Test]
        public async Task Poll_AtTheAnnouncedInterval_IsPending_AndAFastPollIsSlowedDownByFiveSeconds()
        {
            var open = await Open();
            var first = await _svc.PollAsync("chat", open.AuthReqId);
            Assert.That((first.Outcome, first.Interval), Is.EqualTo((PollOutcome.Pending, 2)));

            // Again at once: too fast. The interval grows by 5 seconds (RFC 8628 3.5, CIBA 11).
            var fast = await _svc.PollAsync("chat", open.AuthReqId);
            Assert.That((fast.Outcome, fast.Interval), Is.EqualTo((PollOutcome.SlowDown, 7)));
            var fastAgain = await _svc.PollAsync("chat", open.AuthReqId);
            Assert.That((fastAgain.Outcome, fastAgain.Interval), Is.EqualTo((PollOutcome.SlowDown, 12)));

            // Waiting the new interval is fine, and the interval does not shrink again.
            _time.Now = Start.AddSeconds(12);
            var patient = await _svc.PollAsync("chat", open.AuthReqId);
            Assert.That((patient.Outcome, patient.Interval), Is.EqualTo((PollOutcome.Pending, 12)));
            Assert.That(_store.Records[open.AuthReqId].Interval, Is.EqualTo(12));
        }

        [Test]
        public async Task Poll_OneSecondIntoATwoSecondInterval_IsTooFast()
        {
            var open = await Open();
            await _svc.PollAsync("chat", open.AuthReqId);
            _time.Now = Start.AddSeconds(1);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.SlowDown));
        }

        [Test]
        public async Task Poll_WhenTheIntervalHasPassed_IsPending()
        {
            var open = await Open();
            await _svc.PollAsync("chat", open.AuthReqId);
            _time.Now = Start.AddSeconds(2);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.Pending));
        }

        [Test]
        public async Task Poll_OnlyTheClientThatOpenedTheRequestMayPollIt()
        {
            var open = await Open();
            Assert.That((await _svc.PollAsync("another-client", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.InvalidGrant));
            Assert.That((await _svc.PollAsync("chat", "ciba_unknown")).Outcome, Is.EqualTo(PollOutcome.InvalidGrant));
            Assert.That((await _svc.PollAsync("", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.InvalidGrant));
            Assert.That(_store.Records[open.AuthReqId].NextPollAt, Is.EqualTo(Start.ToUnixTimeSeconds()), "a stranger's poll changes nothing");
        }

        [Test]
        public async Task Poll_MapsEveryDecisionToTheTokenEndpointAnswer()
        {
            var denied = await Open();
            await _svc.SubmitReceiptAsync(_device, denied.AuthReqId, Receipt(denied.AuthReqId, "deny", ""), Issuer);
            Assert.That((await _svc.PollAsync("chat", denied.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.AccessDenied));

            var locked = await Open();
            var wrong = WrongCode(locked.BindingCode);
            for (var i = 0; i < 3; i++)
            {
                await _svc.SubmitReceiptAsync(_device, locked.AuthReqId, Receipt(locked.AuthReqId, "approve", wrong), Issuer);
            }
            Assert.That(_store.Records[locked.AuthReqId].Status, Is.EqualTo(ConsentStatus.CodeLocked));
            Assert.That((await _svc.PollAsync("chat", locked.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.AccessDenied), "denied before the phone even posts code_failed");
            await _svc.SubmitReceiptAsync(_device, locked.AuthReqId, Receipt(locked.AuthReqId, "code_failed", wrong), Issuer);
            Assert.That((await _svc.PollAsync("chat", locked.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.AccessDenied));

            var approved = await Open();
            await _svc.SubmitReceiptAsync(_device, approved.AuthReqId, Receipt(approved.AuthReqId, "approve", approved.BindingCode), Issuer);
            var result = await _svc.PollAsync("chat", approved.AuthReqId);
            Assert.That(result.Outcome, Is.EqualTo(PollOutcome.Approved));
            Assert.That(result.Request!.ReceiptJson, Is.Not.Null);
            Assert.That(result.Request.BoundHash, Is.EqualTo("BJPM"));
            Assert.That(result.Request.TokenClaims!.Select(c => c.Type), Is.EqualTo(new[] { "sub", "sign" }));

            var pending = await Open();
            _time.Now = Start.AddSeconds(301);
            Assert.That((await _svc.PollAsync("chat", pending.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.Expired));
        }

        [Test]
        public async Task Poll_AnApproval_IsRedeemedOnce()
        {
            var open = await Open();
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.Approved));
            Assert.That(_store.Records[open.AuthReqId].TokenIssued, Is.True);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.InvalidGrant), "an auth_req_id yields one token");
        }

        [Test]
        public async Task Poll_ConcurrentRedemptions_HaveExactlyOneWinner()
        {
            var open = await Open();
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => _svc.PollAsync("chat", open.AuthReqId)));
            Assert.That(results.Count(r => r.Outcome == PollOutcome.Approved), Is.EqualTo(1));
            Assert.That(results.Count(r => r.Outcome == PollOutcome.InvalidGrant), Is.EqualTo(11));
        }

        [Test]
        public async Task Poll_AWrongResource_OrARequestWithoutTokenClaims_IsRefusedBeforeAnythingIsConsumed()
        {
            var open = await Open();
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId, "https://other.test")).Outcome, Is.EqualTo(PollOutcome.InvalidTarget));
            Assert.That(_store.Records[open.AuthReqId].TokenIssued, Is.False, "a refused poll consumes nothing");
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId, "https://mcp.biatec.io/tools")).Outcome, Is.EqualTo(PollOutcome.Approved));

            var bare = await Open(c => c.TokenClaims = null);
            await _svc.SubmitReceiptAsync(_device, bare.AuthReqId, Receipt(bare.AuthReqId, "approve", bare.BindingCode), Issuer);
            Assert.That((await _svc.PollAsync("chat", bare.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.InvalidGrant));
            Assert.That(_store.Records[bare.AuthReqId].TokenIssued, Is.False);
        }

        [Test]
        public async Task Poll_AnApprovalCollectedAfterTheWindow_YieldsNothing()
        {
            var open = await Open();
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            _time.Now = Start.AddSeconds(299);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.Approved));
            _time.Now = Start.AddSeconds(300);
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.Expired));
        }

        [Test]
        public async Task Poll_ALostRace_IsRetried_AndNeverRecordsAHalfStep()
        {
            var open = await Open();
            _store.ConflictsToInject = 2;
            Assert.That((await _svc.PollAsync("chat", open.AuthReqId)).Outcome, Is.EqualTo(PollOutcome.Pending));

            var second = await Open();
            _store.ConflictsToInject = 50;
            var gaveUp = await _svc.PollAsync("chat", second.AuthReqId);
            Assert.That(gaveUp.Outcome, Is.EqualTo(PollOutcome.SlowDown), "a storm of competing polls is answered as too fast, never as a decision");
            Assert.That(gaveUp.Interval, Is.GreaterThan(2));
        }

        // ---- receipt retrieval (issue 15: subject-scoped) ----

        private async Task<string> Approved()
        {
            var open = await Open();
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, Receipt(open.AuthReqId, "approve", open.BindingCode), Issuer);
            return open.AuthReqId;
        }

        [Test]
        public async Task GetReceipt_GoesOnlyToTheUserItBelongsToOrItsOwnAuthenticatedClient()
        {
            var id = await Approved();
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-1", ClientId = "chat" }, id)).Lookup, Is.EqualTo(ReceiptLookup.Found));
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { ClientId = "chat", ClientAuthenticated = true }, id)).Lookup, Is.EqualTo(ReceiptLookup.Found));

            // Another user, with a token for the same client: not found, not forbidden (nothing is confirmed).
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-2", ClientId = "chat" }, id)).Lookup, Is.EqualTo(ReceiptLookup.NotFound));
            // The same user, with a token issued to another client.
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-1", ClientId = "other-client" }, id)).Lookup, Is.EqualTo(ReceiptLookup.NotFound));
            // Another client with its own credentials, and a caller that proved nothing about the client.
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { ClientId = "other-client", ClientAuthenticated = true }, id)).Lookup, Is.EqualTo(ReceiptLookup.NotFound));
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { ClientId = "chat" }, id)).Lookup, Is.EqualTo(ReceiptLookup.NotFound), "a client id without a secret proves nothing");
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-1" }, id)).Lookup, Is.EqualTo(ReceiptLookup.NotFound), "a token that names no client");
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller(), id)).Lookup, Is.EqualTo(ReceiptLookup.NotFound));
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-1", ClientId = "chat" }, "ciba_unknown")).Lookup, Is.EqualTo(ReceiptLookup.NotFound));
        }

        [Test]
        public async Task GetReceipt_BeforeADecision_IsNotReady_OnlyForTheOwner()
        {
            var open = await Open();
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-1", ClientId = "chat" }, open.AuthReqId)).Lookup, Is.EqualTo(ReceiptLookup.NotReady));
            Assert.That((await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-2", ClientId = "chat" }, open.AuthReqId)).Lookup, Is.EqualTo(ReceiptLookup.NotFound));
        }

        [Test]
        public async Task GetReceipt_ReturnsTheDenialToo_AndTheStoredBytes()
        {
            var open = await Open();
            var deny = Receipt(open.AuthReqId, "deny", "");
            await _svc.SubmitReceiptAsync(_device, open.AuthReqId, deny, Issuer);
            var (lookup, json) = await _svc.GetReceiptAsync(new ReceiptCaller { Sub = "user-1", ClientId = "chat" }, open.AuthReqId);
            Assert.That(lookup, Is.EqualTo(ReceiptLookup.Found));
            Assert.That(JsonCanonicalizer.Canonicalize(json!), Is.EqualTo(JsonCanonicalizer.Canonicalize(deny)));
        }
    }
}
