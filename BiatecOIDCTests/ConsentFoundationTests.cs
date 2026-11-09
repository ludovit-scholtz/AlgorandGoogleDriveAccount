using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Helper;
using BiatecOIDC.Model;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BiatecOIDCTests
{
    /// <summary>
    /// The cryptographic base of the consent API, checked against the published vectors of the Biatec consent protocol
    /// (copied from scholtz/biatec-chat packages/consent-protocol/vectors) so the .NET server and the other implementations
    /// agree byte for byte.
    /// </summary>
    [TestFixture]
    public class ConsentFoundationTests
    {
        private const string VectorIssuer = "https://oidc.biatec.test";

        private static JsonDocument Load(string name)
            => JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ConsentVectors", name), Encoding.UTF8));

        private static IEnumerable<JsonElement> VectorJwks()
        {
            using var doc = Load("jwks.json");
            return doc.RootElement.GetProperty("keys").EnumerateArray().Select(k => k.Clone()).ToList();
        }

        // ---- RFC 8785 ----

        [Test]
        public void Canonicalization_ReproducesEveryPublishedVector()
        {
            using var doc = Load("canonicalization.json");
            var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray().ToList();
            Assert.That(vectors, Is.Not.Empty);
            foreach (var v in vectors)
            {
                Assert.That(JsonCanonicalizer.Canonicalize(v.GetProperty("input")), Is.EqualTo(v.GetProperty("canonical").GetString()),
                    v.GetProperty("name").GetString());
            }
        }

        [TestCase("0", "0")]
        [TestCase("-0", "0")]
        [TestCase("1", "1")]
        [TestCase("-1.5", "-1.5")]
        [TestCase("1e21", "1e+21")]
        [TestCase("1e20", "100000000000000000000")]
        [TestCase("0.000001", "0.000001")]
        [TestCase("0.0000001", "1e-7")]
        [TestCase("1.2345678901234568e20", "123456789012345680000")]
        [TestCase("1.7976931348623157e308", "1.7976931348623157e+308")]
        [TestCase("5e-324", "5e-324")]
        [TestCase("100", "100")]
        [TestCase("1.0", "1")]
        [TestCase("9007199254740991", "9007199254740991")]
        public void Numbers_FollowTheEcmaScriptFormat(string input, string expected)
            => Assert.That(JsonCanonicalizer.Canonicalize(input), Is.EqualTo(expected));

        [Test]
        public void Canonicalization_RefusesWhatASignatureMustNotCover()
        {
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("9007199254740993"));
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("{\"a\":1,\"a\":2}"));
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("1e999"));
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("{not json"));
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("\"\\ud800\""));
            Assert.Throws<JsonCanonicalizationException>(() => JsonCanonicalizer.Canonicalize("\"\\udc00x\""));
        }

        [Test]
        public void Canonicalization_SortsByUtf16CodeUnitAndEscapesControlCharacters()
        {
            Assert.That(JsonCanonicalizer.Canonicalize("{\"\\ud83d\\ude00\":1,\"\\uffff\":2,\"a\":3}"), Is.EqualTo("{\"a\":3,\"\ud83d\ude00\":1,\"\uffff\":2}"));
            Assert.That(JsonCanonicalizer.Canonicalize("\"\\u007f\\u0001\\u001f\\u2028\""), Is.EqualTo("\"\u007f\\u0001\\u001f\u2028\""));
        }

        // ---- the published receipts ----

        [Test]
        public void EveryPublishedReceipt_VerifiesOffline()
        {
            using var doc = Load("receipts.json");
            var jwks = VectorJwks();
            var vectors = doc.RootElement.GetProperty("vectors").EnumerateArray().ToList();
            Assert.That(vectors.Count, Is.EqualTo(6));
            foreach (var v in vectors)
            {
                var receipt = v.GetProperty("receipt");
                var now = DateTimeOffset.FromUnixTimeSeconds(receipt.GetProperty("issued_at").GetInt64() + 60);
                var name = v.GetProperty("name").GetString();
                Assert.That(JsonCanonicalizer.Canonicalize(receipt.GetProperty("signed")), Is.EqualTo(v.GetProperty("canonical").GetString()), name);
                Assert.That(ConsentReceiptVerifier.VerifyDeviceSignature(receipt), Is.True, name);
                Assert.That(ConsentReceiptVerifier.VerifyAttestation(receipt, jwks, VectorIssuer, now), Is.True, name);
                Assert.That(ConsentReceiptVerifier.VerifyReceipt(receipt, jwks, VectorIssuer, now), Is.True, name);
            }
        }

        [Test]
        public void ChangingAnySignedMember_BreaksTheDeviceSignature()
        {
            using var doc = Load("receipts.json");
            var receiptJson = doc.RootElement.GetProperty("vectors")[0].GetProperty("receipt").GetRawText();
            foreach (var member in new[] { "auth_req_id", "client_entity_id", "binding_code", "decision", "sub", "binding_message" })
            {
                var node = JsonNode.Parse(receiptJson)!;
                node["signed"]![member] = node["signed"]![member]!.GetValue<string>() + "x";
                Assert.That(ConsentReceiptVerifier.VerifyDeviceSignature(JsonDocument.Parse(node.ToJsonString()).RootElement), Is.False, member);
            }
            var expiry = JsonNode.Parse(receiptJson)!;
            expiry["signed"]!["expiry"] = expiry["signed"]!["expiry"]!.GetValue<long>() + 1;
            Assert.That(ConsentReceiptVerifier.VerifyDeviceSignature(JsonDocument.Parse(expiry.ToJsonString()).RootElement), Is.False);
            var details = JsonNode.Parse(receiptJson)!;
            details["signed"]!["authorization_details"]![0]!["amount"] = "5000.000001";
            Assert.That(ConsentReceiptVerifier.VerifyDeviceSignature(JsonDocument.Parse(details.ToJsonString()).RootElement), Is.False);
        }

        [Test]
        public void TheAttestation_IsBoundToIssuerSubDeviceKeyAndTime()
        {
            using var doc = Load("receipts.json");
            var receiptJson = doc.RootElement.GetProperty("vectors")[0].GetProperty("receipt").GetRawText();
            var jwks = VectorJwks();
            var now = DateTimeOffset.FromUnixTimeSeconds(JsonNode.Parse(receiptJson)!["issued_at"]!.GetValue<long>() + 60);
            static JsonElement Parse(JsonNode n) => JsonDocument.Parse(n.ToJsonString()).RootElement;

            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(JsonNode.Parse(receiptJson)!), jwks, "https://other.test", now), Is.False, "issuer");
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(JsonNode.Parse(receiptJson)!), jwks, "", now), Is.False, "issuer is required");
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(JsonNode.Parse(receiptJson)!), jwks, VectorIssuer, now.AddDays(2000)), Is.False, "expired");
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(JsonNode.Parse(receiptJson)!), Array.Empty<JsonElement>(), VectorIssuer, now), Is.False, "unknown key");

            var otherSub = JsonNode.Parse(receiptJson)!;
            otherSub["signed"]!["sub"] = "pw_someone_else";
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(otherSub), jwks, VectorIssuer, now), Is.False, "sub");

            var otherKid = JsonNode.Parse(receiptJson)!;
            otherKid["device"]!["kid"] = "device-other";
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(otherKid), jwks, VectorIssuer, now), Is.False, "device kid");

            using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = other.ExportParameters(false);
            var otherKey = JsonNode.Parse(receiptJson)!;
            otherKey["device"]!["public_key"]!["x"] = Base64UrlEncoder.Encode(p.Q.X!);
            otherKey["device"]!["public_key"]!["y"] = Base64UrlEncoder.Encode(p.Q.Y!);
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(otherKey), jwks, VectorIssuer, now), Is.False, "device key");

            var wrongType = JsonNode.Parse(receiptJson)!;
            var parts = wrongType["device"]!["attestation"]!.GetValue<string>().Split('.');
            wrongType["device"]!["attestation"] = Base64UrlEncoder.Encode("{\"alg\":\"ES256\",\"kid\":\"oidc-test-key-1\",\"typ\":\"JWT\"}") + "." + parts[1] + "." + parts[2];
            Assert.That(ConsentReceiptVerifier.VerifyAttestation(Parse(wrongType), jwks, VectorIssuer, now), Is.False, "typ");
        }

        [Test]
        public void Verification_NeverThrows_OnMalformedInput()
        {
            var junk = new[]
            {
                "{}", "[]", "null", "\"x\"", "1",
                "{\"alg\":\"ES256\"}",
                "{\"alg\":\"ES256\",\"signed\":{},\"signature\":\"\",\"device\":{}}",
                "{\"alg\":\"ES256\",\"signed\":{\"a\":1e999},\"signature\":\"AAAA\",\"device\":{\"public_key\":{\"kty\":\"EC\",\"crv\":\"P-256\",\"x\":\"AA\",\"y\":\"AA\"},\"attestation\":\"a.b.c\"}}",
                "{\"signed\":{\"sub\":1},\"device\":{\"kid\":5,\"public_key\":7,\"attestation\":{}}}",
            };
            foreach (var j in junk)
            {
                var el = JsonDocument.Parse(j).RootElement;
                Assert.DoesNotThrow(() =>
                {
                    Assert.That(ConsentReceiptVerifier.VerifyDeviceSignature(el), Is.False, j);
                    Assert.That(ConsentReceiptVerifier.VerifyAttestation(el, VectorJwks(), VectorIssuer, DateTimeOffset.UtcNow), Is.False, j);
                }, j);
            }
        }

        [TestCase("AAAA", true)]
        [TestCase("AA+A", false)]
        [TestCase("AA==", false)]
        [TestCase("AA A", false)]
        [TestCase("", true)]
        public void Base64Url_RefusesPaddingAndForeignCharacters(string text, bool expected)
            => Assert.That(ConsentReceiptVerifier.TryDecodeBase64Url(text, out _), Is.EqualTo(expected));

        [Test]
        public void Base64Url_RefusesNonZeroTrailingBits()
        {
            // 43 characters carry 258 bits for 32 bytes: the last character may only use the top 4 bits.
            var canonical = Base64UrlEncoder.Encode(new byte[32]);
            Assert.That(canonical, Does.EndWith("A"));
            Assert.That(ConsentReceiptVerifier.TryDecodeBase64Url(canonical, out var bytes), Is.True);
            Assert.That(bytes.Length, Is.EqualTo(32));
            var sloppy = canonical[..^1] + "B";
            Assert.That(ConsentReceiptVerifier.TryDecodeBase64Url(sloppy, out _), Is.False);
        }

        // ---- the attestation Biatec OIDC issues itself ----

        private static ConsentAttestationService Service(out string pem)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            pem = key.ExportPkcs8PrivateKeyPem();
            return new ConsentAttestationService(Options.Create(new ConsentConfiguration { AttestationSigningKeyPem = pem }));
        }

        [Test]
        public void IssuedAttestation_VerifiesWithThePublishedJwksKey()
        {
            using var svc = Service(out _);
            using var device = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var p = device.ExportParameters(false);
            var x = Base64UrlEncoder.Encode(p.Q.X!);
            var y = Base64UrlEncoder.Encode(p.Q.Y!);
            var now = DateTimeOffset.UtcNow;
            var attestation = svc.Issue("https://oidc.test/", "sub-1", "device-0001", x, y, now)!;

            var signed = new JsonObject
            {
                ["auth_req_id"] = "ciba_1", ["binding_code"] = "123456", ["binding_message"] = "", ["client_entity_id"] = "https://chat.test",
                ["decision"] = "approve", ["expiry"] = now.ToUnixTimeSeconds() + 300, ["sub"] = "sub-1",
                ["authorization_details"] = new JsonArray(new JsonObject { ["type"] = "biatec_transaction", ["amount"] = "1.000000" }),
            };
            var signature = device.SignData(JsonCanonicalizer.CanonicalizeToUtf8(JsonDocument.Parse(signed.ToJsonString()).RootElement),
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            var receipt = new JsonObject
            {
                ["version"] = "1", ["alg"] = "ES256", ["signed"] = signed, ["signature"] = Base64UrlEncoder.Encode(signature), ["issued_at"] = now.ToUnixTimeSeconds(),
                ["device"] = new JsonObject
                {
                    ["kid"] = "device-0001", ["attestation"] = attestation,
                    ["public_key"] = new JsonObject { ["kty"] = "EC", ["crv"] = "P-256", ["x"] = x, ["y"] = y },
                },
            };
            var el = JsonDocument.Parse(receipt.ToJsonString()).RootElement;
            var jwks = new[] { JsonDocument.Parse(svc.GetPublicJwk()!.ToJsonString()).RootElement };

            Assert.That(ConsentReceiptVerifier.VerifyReceipt(el, jwks, "https://oidc.test", now.AddSeconds(5)), Is.True);
            Assert.That(ConsentReceiptVerifier.VerifyReceipt(el, jwks, "https://oidc.test", now.AddDays(401)), Is.False, "expires after 400 days");
            Assert.That(svc.GetPublicJwk()!.ToJsonString(), Does.Not.Contain("\"d\""), "no private part in the JWKS");
        }

        [Test]
        public void WithoutAKey_TheServiceIsNotConfigured_AndIssuesNothing()
        {
            using var svc = new ConsentAttestationService(Options.Create(new ConsentConfiguration()));
            Assert.That(svc.IsConfigured, Is.False);
            Assert.That(svc.Issue("https://oidc.test", "s", "device-0001", "x", "y", DateTimeOffset.UtcNow), Is.Null);
            Assert.That(svc.GetPublicJwk(), Is.Null);
        }

        [Test]
        public void AKeyThatIsNotP256_IsRefusedAtStartup()
        {
            using var wrong = ECDsa.Create(ECCurve.NamedCurves.nistP384);
            var cfg = new ConsentConfiguration { AttestationSigningKeyPem = wrong.ExportPkcs8PrivateKeyPem() };
            Assert.Throws<InvalidOperationException>(() => new ConsentAttestationService(Options.Create(cfg)));
            Assert.Catch(() => new ConsentAttestationService(Options.Create(new ConsentConfiguration { AttestationSigningKeyPem = "not a pem" })));
        }

        [Test]
        public void Thumbprint_MatchesTheRfc7638Vector()
        {
            // RFC 7638 3.1 uses an RSA key; for EC the same rule applies with crv, kty, x, y. The published receipt vector
            // carries the expected value for its device key.
            using var doc = Load("receipts.json");
            var receipt = doc.RootElement.GetProperty("vectors")[0].GetProperty("receipt");
            var key = receipt.GetProperty("device").GetProperty("public_key");
            var parts = receipt.GetProperty("device").GetProperty("attestation").GetString()!.Split('.');
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            Assert.That(ConsentAttestationService.Thumbprint(key.GetProperty("x").GetString()!, key.GetProperty("y").GetString()!),
                Is.EqualTo(payload.RootElement.GetProperty("device_jwk_thumbprint").GetString()));
        }
    }
}
