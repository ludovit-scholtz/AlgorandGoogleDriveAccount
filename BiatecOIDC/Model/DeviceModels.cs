using System.Text.Json.Serialization;

namespace BiatecOIDC.Model
{
    /// <summary>An enrolled authenticator device (Biatec Authenticator). Holds the public half of its signing key only.</summary>
    public sealed class DeviceRecord
    {
        public string Kid { get; set; } = "";
        public string Sub { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>P-256 public key, base64url x and y.</summary>
        public string KeyX { get; set; } = "";
        public string KeyY { get; set; } = "";
        /// <summary>SHA-256 (hex) of the device token. The token itself is never stored.</summary>
        public string TokenHash { get; set; } = "";
        public long CreatedAt { get; set; }
        /// <summary>Epoch seconds when the device becomes active (rule 7 activation delay).</summary>
        public long ActiveAt { get; set; }
        public string? PushProvider { get; set; }
        public string? PushToken { get; set; }
    }

    /// <summary>A P-256 public key as a JWK.</summary>
    public sealed class DeviceJwk
    {
        [JsonPropertyName("kty")] public string? Kty { get; set; }
        [JsonPropertyName("crv")] public string? Crv { get; set; }
        [JsonPropertyName("x")] public string? X { get; set; }
        [JsonPropertyName("y")] public string? Y { get; set; }
    }

    /// <summary>Where a device's notifications go.</summary>
    public sealed class DevicePush
    {
        [JsonPropertyName("provider")] public string? Provider { get; set; }
        [JsonPropertyName("token")] public string? Token { get; set; }
    }

    /// <summary><c>POST /devices</c> body.</summary>
    public sealed class EnrollDeviceRequest
    {
        [JsonPropertyName("kid")] public string? Kid { get; set; }
        [JsonPropertyName("public_key")] public DeviceJwk? PublicKey { get; set; }
        [JsonPropertyName("push")] public DevicePush? Push { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
    }

    /// <summary><c>POST /devices</c> answer.</summary>
    public sealed class EnrollDeviceResponse
    {
        [JsonPropertyName("kid")] public string Kid { get; set; } = "";
        [JsonPropertyName("active")] public bool Active { get; set; }
        [JsonPropertyName("active_at")] public long ActiveAt { get; set; }
        [JsonPropertyName("device_token")] public string DeviceToken { get; set; } = "";
    }

    /// <summary>Settings of the device API (section <c>Devices</c>).</summary>
    public sealed class DevicesConfiguration
    {
        /// <summary>Seconds between enrollment and activation (protocol rule 7). Default 24 h.</summary>
        public int ActivationSeconds { get; set; } = 24 * 60 * 60;
        /// <summary>Most devices one user can have.</summary>
        public int MaxDevicesPerUser { get; set; } = 10;
        /// <summary><c>PUT /devices/push</c> calls allowed per device and minute.</summary>
        public int PushRegistrationsPerMinute { get; set; } = 6;
        /// <summary>Accepted clock skew of the proof's <c>iat</c>, in seconds.</summary>
        public int ProofSkewSeconds { get; set; } = 60;
    }
}
