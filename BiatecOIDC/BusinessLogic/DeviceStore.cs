using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiatecOIDC.Model;
using StackExchange.Redis;

namespace BiatecOIDC.BusinessLogic
{
    /// <inheritdoc cref="IDeviceStore"/>
    public sealed class DeviceStore : IDeviceStore
    {
        private const string DevicePrefix = "oidc:device:";
        private const string TokenPrefix = "oidc:device-token:";
        private const string UserPrefix = "oidc:device-user:";
        private const string PushPrefix = "oidc:device-push:";
        private const string ProofPrefix = "oidc:device-proof:";
        private const string RatePrefix = "oidc:device-pushrate:";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly IConnectionMultiplexer _redis;

        public DeviceStore(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<bool> TryAddAsync(DeviceRecord device)
        {
            var db = _redis.GetDatabase();
            if (!await db.StringSetAsync(DevicePrefix + device.Kid, JsonSerializer.Serialize(device, JsonOptions), when: When.NotExists))
            {
                return false;
            }
            await db.StringSetAsync(TokenPrefix + device.TokenHash, device.Kid);
            await db.SetAddAsync(UserPrefix + device.Sub, device.Kid);
            if (!string.IsNullOrEmpty(device.PushProvider) && !string.IsNullOrEmpty(device.PushToken))
            {
                await SetPushAsync(device.Kid, device.PushProvider, device.PushToken);
            }
            return true;
        }

        public async Task<DeviceRecord?> GetByKidAsync(string kid)
        {
            var value = await _redis.GetDatabase().StringGetAsync(DevicePrefix + kid);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<DeviceRecord>((string)value!, JsonOptions);
        }

        public async Task<DeviceRecord?> GetByTokenHashAsync(string tokenHash)
        {
            var kid = await _redis.GetDatabase().StringGetAsync(TokenPrefix + tokenHash);
            return kid.IsNullOrEmpty ? null : await GetByKidAsync(kid!);
        }

        public async Task<IReadOnlyList<DeviceRecord>> ListForUserAsync(string sub)
        {
            var kids = await _redis.GetDatabase().SetMembersAsync(UserPrefix + sub);
            var devices = new List<DeviceRecord>();
            foreach (var kid in kids)
            {
                if (await GetByKidAsync(kid!) is { } device)
                {
                    devices.Add(device);
                }
            }
            return devices;
        }

        public async Task<int> CountForUserAsync(string sub)
            => (int)await _redis.GetDatabase().SetLengthAsync(UserPrefix + sub);

        public async Task SetPushAsync(string kid, string provider, string token)
        {
            var db = _redis.GetDatabase();
            var key = PushPrefix + provider + ":" + Hash(token);
            var previous = await db.StringGetSetAsync(key, kid);
            if (!previous.IsNullOrEmpty && previous != kid)
            {
                var other = await GetByKidAsync(previous!);
                if (other is not null && other.PushToken == token && other.PushProvider == provider)
                {
                    other.PushProvider = null;
                    other.PushToken = null;
                    await db.StringSetAsync(DevicePrefix + other.Kid, JsonSerializer.Serialize(other, JsonOptions));
                }
            }

            var device = await GetByKidAsync(kid);
            if (device is null)
            {
                return;
            }
            if (!string.IsNullOrEmpty(device.PushToken) && (device.PushToken != token || device.PushProvider != provider))
            {
                await db.KeyDeleteAsync(PushPrefix + device.PushProvider + ":" + Hash(device.PushToken));
            }
            device.PushProvider = provider;
            device.PushToken = token;
            await db.StringSetAsync(DevicePrefix + kid, JsonSerializer.Serialize(device, JsonOptions));
        }

        public Task<bool> TryRememberProofAsync(string kid, string jti, TimeSpan ttl)
            => _redis.GetDatabase().StringSetAsync(ProofPrefix + kid + ":" + Hash(jti), "1", ttl, When.NotExists);

        public async Task<int> CountPushRegistrationAsync(string kid)
        {
            var db = _redis.GetDatabase();
            var key = RatePrefix + kid + ":" + DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60;
            var count = await db.StringIncrementAsync(key);
            if (count == 1)
            {
                await db.KeyExpireAsync(key, TimeSpan.FromMinutes(2));
            }
            return (int)count;
        }

        /// <summary>SHA-256 hex, so a push token or jti never sits in a key in the clear.</summary>
        public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
