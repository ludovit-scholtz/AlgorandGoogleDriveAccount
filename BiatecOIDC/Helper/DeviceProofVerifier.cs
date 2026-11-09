using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace BiatecOIDC.Helper
{
    /// <summary>Why a device proof was refused.</summary>
    public enum DeviceProofResult
    {
        Valid,
        Missing,
        Malformed,
        BadHeader,
        BadSignature,
        MethodMismatch,
        UrlMismatch,
        TokenMismatch,
        Stale,
        BadJti,
    }

    /// <summary>
    /// Verifies <c>X-Device-Proof</c>: a DPoP-style (RFC 9449) compact JWS, header <c>alg</c> ES256 and <c>typ</c>
    /// <c>biatec-device-proof+jwt</c> with <c>kid</c> = the device kid, signed with the enrolled device key (raw r||s).
    /// Claims: <c>htm</c>, <c>htu</c>, <c>iat</c>, <c>jti</c> (16 to 128 characters) and <c>ath</c> (base64url SHA-256 of the device token).
    /// The caller still has to remember the <c>jti</c> to refuse a replay.
    /// </summary>
    public static class DeviceProofVerifier
    {
        public const string ProofType = "biatec-device-proof+jwt";
        public const int MinJtiLength = 16;
        public const int MaxJtiLength = 128;

        /// <summary>Checks the proof; <paramref name="jti"/> is set when the result is <see cref="DeviceProofResult.Valid"/>.</summary>
        public static DeviceProofResult Verify(string? proof, string kid, string keyX, string keyY, string method, string url,
            string deviceToken, DateTimeOffset now, int skewSeconds, out string? jti)
        {
            jti = null;
            if (string.IsNullOrWhiteSpace(proof))
            {
                return DeviceProofResult.Missing;
            }

            var parts = proof.Split('.');
            if (parts.Length != 3)
            {
                return DeviceProofResult.Malformed;
            }

            byte[] header, payload, signature;
            try
            {
                header = Base64UrlEncoder.DecodeBytes(parts[0]);
                payload = Base64UrlEncoder.DecodeBytes(parts[1]);
                signature = Base64UrlEncoder.DecodeBytes(parts[2]);
            }
            catch (FormatException)
            {
                return DeviceProofResult.Malformed;
            }

            try
            {
                using var headerDoc = JsonDocument.Parse(header);
                var h = headerDoc.RootElement;
                if (h.ValueKind != JsonValueKind.Object
                    || GetString(h, "alg") != "ES256"
                    || GetString(h, "typ") != ProofType
                    || GetString(h, "kid") != kid)
                {
                    return DeviceProofResult.BadHeader;
                }

                if (signature.Length != 64 || !SignatureValid(parts[0] + "." + parts[1], signature, keyX, keyY))
                {
                    return DeviceProofResult.BadSignature;
                }

                using var claimsDoc = JsonDocument.Parse(payload);
                var c = claimsDoc.RootElement;
                if (c.ValueKind != JsonValueKind.Object)
                {
                    return DeviceProofResult.Malformed;
                }

                if (!string.Equals(GetString(c, "htm"), method, StringComparison.OrdinalIgnoreCase))
                {
                    return DeviceProofResult.MethodMismatch;
                }

                if (!UrlsEqual(GetString(c, "htu"), url))
                {
                    return DeviceProofResult.UrlMismatch;
                }

                var ath = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.UTF8.GetBytes(deviceToken)));
                if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(GetString(c, "ath") ?? ""), Encoding.ASCII.GetBytes(ath)))
                {
                    return DeviceProofResult.TokenMismatch;
                }

                if (!c.TryGetProperty("iat", out var iatEl) || iatEl.ValueKind != JsonValueKind.Number || !iatEl.TryGetInt64(out var iat)
                    || Math.Abs(now.ToUnixTimeSeconds() - iat) > skewSeconds)
                {
                    return DeviceProofResult.Stale;
                }

                var id = GetString(c, "jti");
                if (id is null || id.Length < MinJtiLength || id.Length > MaxJtiLength)
                {
                    return DeviceProofResult.BadJti;
                }

                jti = id;
                return DeviceProofResult.Valid;
            }
            catch (JsonException)
            {
                return DeviceProofResult.Malformed;
            }
        }

        private static string? GetString(JsonElement o, string name)
            => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static bool SignatureValid(string signingInput, byte[] signature, string keyX, string keyY)
        {
            try
            {
                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Base64UrlEncoder.DecodeBytes(keyX), Y = Base64UrlEncoder.DecodeBytes(keyY) },
                });
                return ecdsa.VerifyData(Encoding.ASCII.GetBytes(signingInput), signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or PlatformNotSupportedException)
            {
                return false;
            }
        }

        /// <summary>Compares the URL called with <c>htu</c>, ignoring a query, a fragment and a trailing slash (RFC 9449 4.3).</summary>
        private static bool UrlsEqual(string? htu, string url)
        {
            static string Normalize(string s)
            {
                var cut = s.IndexOfAny(new[] { '?', '#' });
                if (cut >= 0)
                {
                    s = s[..cut];
                }
                return s.TrimEnd('/');
            }
            return htu is not null && string.Equals(Normalize(htu), Normalize(url), StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>True when the base64url x and y are a valid P-256 point.</summary>
        public static bool IsValidP256Key(string? x, string? y)
        {
            if (string.IsNullOrEmpty(x) || string.IsNullOrEmpty(y))
            {
                return false;
            }
            try
            {
                using var ecdsa = ECDsa.Create(new ECParameters
                {
                    Curve = ECCurve.NamedCurves.nistP256,
                    Q = new ECPoint { X = Base64UrlEncoder.DecodeBytes(x), Y = Base64UrlEncoder.DecodeBytes(y) },
                });
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException or PlatformNotSupportedException)
            {
                return false;
            }
        }
    }
}
