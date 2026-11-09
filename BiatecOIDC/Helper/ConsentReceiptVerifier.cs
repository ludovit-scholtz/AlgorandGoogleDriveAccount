using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiatecOIDC.BusinessLogic;
using Microsoft.IdentityModel.Tokens;

namespace BiatecOIDC.Helper
{
    /// <summary>
    /// Offline verification of a consent receipt (consent protocol section 8.1, steps 2 to 4): the attestation JWS against
    /// the Biatec OIDC JWKS and the device signature over the RFC 8785 form of <c>signed</c>. Every method returns false on
    /// malformed input and never throws: a receipt that cannot be read is not a consent.
    /// </summary>
    public static class ConsentReceiptVerifier
    {
        /// <summary>Strict unpadded base64url: refuses stray characters and non-zero trailing bits.</summary>
        public static bool TryDecodeBase64Url(string? text, out byte[] bytes)
        {
            bytes = Array.Empty<byte>();
            if (text is null)
            {
                return false;
            }
            foreach (var c in text)
            {
                var ok = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok)
                {
                    return false;
                }
            }
            try
            {
                var decoded = Base64UrlEncoder.DecodeBytes(text);
                if (Base64UrlEncoder.Encode(decoded) != text)
                {
                    return false;
                }
                bytes = decoded;
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        /// <summary>ES256 over <paramref name="data"/>: a 64-byte r||s signature by the P-256 key x, y.</summary>
        public static bool VerifyEs256(string? x, string? y, byte[] data, string? signature)
        {
            try
            {
                if (!TryDecodeBase64Url(x, out var xb) || !TryDecodeBase64Url(y, out var yb) || !TryDecodeBase64Url(signature, out var sig)
                    || xb.Length != 32 || yb.Length != 32 || sig.Length != 64)
                {
                    return false;
                }
                using var ecdsa = ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = xb, Y = yb } });
                return ecdsa.VerifyData(data, sig, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        /// <summary>Verifies the device signature over JCS(<c>signed</c>) with the key in <c>device.public_key</c>.</summary>
        public static bool VerifyDeviceSignature(JsonElement receipt)
        {
            try
            {
                if (Str(receipt, "alg") != "ES256" || !receipt.TryGetProperty("signed", out var signed) || !receipt.TryGetProperty("device", out var device)
                    || !device.TryGetProperty("public_key", out var key) || Str(key, "kty") != "EC" || Str(key, "crv") != "P-256")
                {
                    return false;
                }
                return VerifyEs256(Str(key, "x"), Str(key, "y"), JsonCanonicalizer.CanonicalizeToUtf8(signed), Str(receipt, "signature"));
            }
            catch (Exception ex) when (ex is JsonCanonicalizationException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Verifies the attestation with <paramref name="jwksKeys"/> and that it binds this receipt's <c>sub</c> and device key.
        /// The issuer is required, the attestation must not be expired at <paramref name="now"/> (a trusted clock), and the signing key
        /// must be an EC P-256 signature key.
        /// </summary>
        public static bool VerifyAttestation(JsonElement receipt, IEnumerable<JsonElement> jwksKeys, string issuer, DateTimeOffset now)
        {
            try
            {
                if (string.IsNullOrEmpty(issuer) || !receipt.TryGetProperty("device", out var device))
                {
                    return false;
                }
                var parts = (Str(device, "attestation") ?? "").Split('.');
                if (parts.Length != 3 || !TryDecodeBase64Url(parts[0], out var hb) || !TryDecodeBase64Url(parts[1], out var pb))
                {
                    return false;
                }

                using var headerDoc = JsonDocument.Parse(hb);
                using var payloadDoc = JsonDocument.Parse(pb);
                var header = headerDoc.RootElement;
                var payload = payloadDoc.RootElement;
                var kid = Str(header, "kid");
                if (string.IsNullOrEmpty(kid) || Str(header, "alg") != "ES256" || Str(header, "typ") != ConsentAttestationService.AttestationType)
                {
                    return false;
                }

                JsonElement? key = null;
                foreach (var k in jwksKeys)
                {
                    if (Str(k, "kid") == kid)
                    {
                        key = k;
                        break;
                    }
                }
                if (key is not { } jwk || Str(jwk, "kty") != "EC" || Str(jwk, "crv") != "P-256" || (Str(jwk, "use") is { } use && use != "sig"))
                {
                    return false;
                }

                if (!VerifyEs256(Str(jwk, "x"), Str(jwk, "y"), Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]), parts[2]))
                {
                    return false;
                }

                if (Str(payload, "iss") != issuer.TrimEnd('/')
                    || !payload.TryGetProperty("exp", out var exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out var expSeconds)
                    || expSeconds <= now.ToUnixTimeSeconds())
                {
                    return false;
                }

                var signed = receipt.GetProperty("signed");
                var pub = device.GetProperty("public_key");
                return Str(payload, "sub") is { } sub && sub == Str(signed, "sub")
                    && Str(payload, "device_kid") == Str(device, "kid")
                    && Str(payload, "device_jwk_thumbprint") == ConsentAttestationService.Thumbprint(Str(pub, "x") ?? "", Str(pub, "y") ?? "");
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException)
            {
                return false;
            }
        }

        /// <summary>Both signatures (rule 9). Says nothing about the decision or the expiry of the consent.</summary>
        public static bool VerifyReceipt(JsonElement receipt, IEnumerable<JsonElement> jwksKeys, string issuer, DateTimeOffset now)
            => VerifyDeviceSignature(receipt) && VerifyAttestation(receipt, jwksKeys, issuer, now);

        private static string? Str(JsonElement o, string name)
            => o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}
