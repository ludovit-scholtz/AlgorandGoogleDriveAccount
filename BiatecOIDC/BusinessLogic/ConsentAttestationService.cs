using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BiatecOIDC.Model;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Issues the device attestation of the Biatec consent protocol: a compact ES256 JWS, signed by a Biatec OIDC key
    /// published in the JWKS, whose payload binds <c>sub</c>, the device kid and the RFC 7638 thumbprint of the device
    /// public key. The device sends it back inside every receipt so a receipt verifies offline.
    /// </summary>
    public interface IConsentAttestationService
    {
        /// <summary>False when no signing key is configured; consent endpoints then fail closed.</summary>
        bool IsConfigured { get; }

        /// <summary>The attestation, or null when not configured.</summary>
        string? Issue(string issuer, string sub, string deviceKid, string keyX, string keyY, DateTimeOffset now);

        /// <summary>The public key as a JWK for the JWKS document, or null when not configured.</summary>
        JsonObject? GetPublicJwk();
    }

    /// <inheritdoc cref="IConsentAttestationService"/>
    public sealed class ConsentAttestationService : IConsentAttestationService, IDisposable
    {
        public const string AttestationType = "biatec-device-attestation+jwt";

        private readonly ECDsa? _key;
        private readonly ConsentConfiguration _config;

        public ConsentAttestationService(IOptions<ConsentConfiguration> options)
        {
            _config = options.Value;
            if (!string.IsNullOrWhiteSpace(_config.AttestationSigningKeyPem))
            {
                var key = ECDsa.Create();
                try
                {
                    key.ImportFromPem(_config.AttestationSigningKeyPem);
                    // Fail at startup, not on the first receipt, when the key is not what the protocol needs.
                    if (key.KeySize != 256 || key.ExportParameters(false).Curve.Oid.FriendlyName is not ("nistP256" or "ECDSA_P256"))
                    {
                        throw new InvalidOperationException("Consent:AttestationSigningKeyPem must be an EC P-256 private key.");
                    }
                }
                catch
                {
                    key.Dispose();
                    throw;
                }
                _key = key;
            }
        }

        public bool IsConfigured => _key is not null;

        public string? Issue(string issuer, string sub, string deviceKid, string keyX, string keyY, DateTimeOffset now)
        {
            if (_key is null)
            {
                return null;
            }

            var header = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["alg"] = "ES256",
                ["kid"] = _config.AttestationKeyId,
                ["typ"] = AttestationType,
            });
            var payload = JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["iss"] = issuer.TrimEnd('/'),
                ["sub"] = sub,
                ["device_kid"] = deviceKid,
                ["device_jwk_thumbprint"] = Thumbprint(keyX, keyY),
                ["iat"] = now.ToUnixTimeSeconds(),
                ["exp"] = now.AddDays(_config.AttestationLifetimeDays).ToUnixTimeSeconds(),
            });
            var input = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(header)) + "." + Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payload));
            var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            return input + "." + Base64UrlEncoder.Encode(signature);
        }

        public JsonObject? GetPublicJwk()
        {
            if (_key is null)
            {
                return null;
            }
            var p = _key.ExportParameters(false);
            return new JsonObject
            {
                ["kty"] = "EC",
                ["crv"] = "P-256",
                ["use"] = "sig",
                ["alg"] = "ES256",
                ["kid"] = _config.AttestationKeyId,
                ["x"] = Base64UrlEncoder.Encode(p.Q.X!),
                ["y"] = Base64UrlEncoder.Encode(p.Q.Y!),
            };
        }

        /// <summary>RFC 7638 thumbprint of an EC P-256 public key: SHA-256 of the members crv, kty, x, y in that order.</summary>
        public static string Thumbprint(string x, string y)
        {
            var json = "{\"crv\":\"P-256\",\"kty\":\"EC\",\"x\":" + JsonSerializer.Serialize(x) + ",\"y\":" + JsonSerializer.Serialize(y) + "}";
            return Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
        }

        public void Dispose() => _key?.Dispose();
    }
}
