using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BiatecOIDC.Model;
using StackExchange.Redis;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Redis implementation of <see cref="IConsentStore"/>. Each multi-key change runs as one Lua script, so it is atomic on the
    /// server. The scripts touch several keys, so Redis must be a single node (or the keys must share a hash slot), as for the
    /// other stores of this service.
    /// </summary>
    public sealed class ConsentStore : IConsentStore
    {
        private const string RecordPrefix = "oidc:consent:";
        private const string UserOpenPrefix = "oidc:consent-open:";
        private const string OriginOpenPrefix = "oidc:consent-origin:";
        private const string RatePrefix = "oidc:consent-rate:";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        // KEYS: 1 record, 2 user open set, 3 client+user+origin open set, 4 hourly counter
        // ARGV: 1 json, 2 id, 3 now, 4 expiry, 5 max per user, 6 max per origin, 7 max per hour, 8 record ttl seconds
        // Returns 0 opened, 1 user cap, 2 origin cap, 3 rate limited, 4 id collision.
        private const string OpenScript = @"
            redis.call('ZREMRANGEBYSCORE', KEYS[2], '-inf', ARGV[3])
            redis.call('ZREMRANGEBYSCORE', KEYS[3], '-inf', ARGV[3])
            if redis.call('ZCARD', KEYS[2]) >= tonumber(ARGV[5]) then return 1 end
            if redis.call('ZCARD', KEYS[3]) >= tonumber(ARGV[6]) then return 2 end
            local n = tonumber(redis.call('GET', KEYS[4]) or '0')
            if n >= tonumber(ARGV[7]) then return 3 end
            if not redis.call('SET', KEYS[1], ARGV[1], 'NX', 'EX', ARGV[8]) then return 4 end
            redis.call('ZADD', KEYS[2], ARGV[4], ARGV[2])
            redis.call('ZADD', KEYS[3], ARGV[4], ARGV[2])
            if redis.call('INCR', KEYS[4]) == 1 then redis.call('EXPIRE', KEYS[4], 3700) end
            return 0";

        // KEYS: 1 record. ARGV: 1 expected version, 2 new json. Returns 1 when replaced.
        private const string UpdateScript = @"
            local cur = redis.call('GET', KEYS[1])
            if not cur then return 0 end
            if cjson.decode(cur).version ~= tonumber(ARGV[1]) then return 0 end
            redis.call('SET', KEYS[1], ARGV[2], 'KEEPTTL')
            return 1";

        private readonly IConnectionMultiplexer _redis;

        public ConsentStore(IConnectionMultiplexer redis)
        {
            _redis = redis;
        }

        public async Task<OpenOutcome> TryOpenAsync(ConsentRequestRecord record, ConsentLimits limits, DateTimeOffset now)
        {
            var nowSeconds = now.ToUnixTimeSeconds();
            var ttl = Math.Max(1, record.Expiry - nowSeconds + limits.RetentionSeconds);
            var result = (long)await _redis.GetDatabase().ScriptEvaluateAsync(OpenScript,
                new RedisKey[]
                {
                    RecordPrefix + record.AuthReqId,
                    UserOpenPrefix + record.Sub,
                    OriginKey(record),
                    RatePrefix + Hash(record.ClientId + "|" + record.Sub) + ":" + nowSeconds / 3600,
                },
                new RedisValue[]
                {
                    JsonSerializer.Serialize(record, JsonOptions),
                    record.AuthReqId,
                    nowSeconds,
                    record.Expiry,
                    limits.MaxOpenPerUser,
                    limits.MaxPendingPerClientOrigin,
                    limits.MaxPerClientUserHour,
                    ttl,
                });
            return result switch
            {
                0 => OpenOutcome.Opened,
                1 => OpenOutcome.UserCap,
                2 => OpenOutcome.OriginCap,
                3 => OpenOutcome.RateLimited,
                _ => OpenOutcome.IdCollision,
            };
        }

        public async Task<ConsentRequestRecord?> GetAsync(string authReqId)
        {
            var value = await _redis.GetDatabase().StringGetAsync(RecordPrefix + authReqId);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<ConsentRequestRecord>((string)value!, JsonOptions);
        }

        public async Task<bool> TryUpdateAsync(ConsentRequestRecord expected, ConsentRequestRecord updated)
        {
            updated.Version = expected.Version + 1;
            var result = (long)await _redis.GetDatabase().ScriptEvaluateAsync(UpdateScript,
                new RedisKey[] { RecordPrefix + expected.AuthReqId },
                new RedisValue[] { expected.Version, JsonSerializer.Serialize(updated, JsonOptions) });
            return result == 1;
        }

        public async Task<IReadOnlyList<string>> ListOpenIdsAsync(string sub, DateTimeOffset now)
        {
            var db = _redis.GetDatabase();
            var key = UserOpenPrefix + sub;
            await db.SortedSetRemoveRangeByScoreAsync(key, double.NegativeInfinity, now.ToUnixTimeSeconds());
            var ids = await db.SortedSetRangeByScoreAsync(key);
            return ids.Select(i => (string)i!).ToList();
        }

        public async Task ReleaseAsync(ConsentRequestRecord record)
        {
            var db = _redis.GetDatabase();
            await db.SortedSetRemoveAsync(UserOpenPrefix + record.Sub, record.AuthReqId);
            await db.SortedSetRemoveAsync(OriginKey(record), record.AuthReqId);
        }

        private static string OriginKey(ConsentRequestRecord r) => OriginOpenPrefix + Hash(r.ClientId + "|" + r.Sub + "|" + r.Origin);

        private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
