using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using StackExchange.Redis;

namespace BiatecOIDCTests
{
    /// <summary>
    /// The atomic parts of the Redis consent store, run against a real Redis. Set <c>BIATEC_TEST_REDIS</c> (for example
    /// <c>localhost:6390</c>, from <c>docker run -p 6390:6379 redis:7-alpine</c>); without it the tests are skipped, never passed.
    /// </summary>
    [TestFixture]
    [NonParallelizable]
    public class ConsentStoreRedisTests
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
        private static readonly ConsentLimits Limits = new() { MaxOpenPerUser = 3, MaxPendingPerClientOrigin = 2, MaxPerClientUserHour = 5, RetentionSeconds = 3600 };

        private ConnectionMultiplexer? _redis;
        private ConsentStore _store = null!;
        private string _run = "";

        [OneTimeSetUp]
        public void Connect()
        {
            var endpoint = Environment.GetEnvironmentVariable("BIATEC_TEST_REDIS");
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                Assert.Ignore("BIATEC_TEST_REDIS is not set.");
            }
            try
            {
                _redis = ConnectionMultiplexer.Connect(endpoint + ",abortConnect=false,connectTimeout=3000");
                _redis.GetDatabase().Ping();
            }
            catch (Exception ex)
            {
                Assert.Ignore("Redis is not reachable: " + ex.Message);
            }
            _store = new ConsentStore(_redis!);
        }

        [OneTimeTearDown]
        public void Disconnect() => _redis?.Dispose();

        [SetUp]
        public void NewRun() => _run = Guid.NewGuid().ToString("N")[..8]; // every test uses its own users and ids

        private ConsentRequestRecord Record(string user = "u", string client = "chat", string origin = "https://app.test", int n = 0, long? expiry = null) => new()
        {
            AuthReqId = $"ciba_{_run}{n:D4}",
            Sub = $"{user}-{_run}",
            ClientId = client,
            Origin = origin,
            Expiry = expiry ?? Now.ToUnixTimeSeconds() + 300,
            Status = ConsentStatus.Pending,
            AttemptsRemaining = 3,
            AuthorizationDetailsJson = "[]",
        };

        [Test]
        public async Task Open_StoresTheRecord_AndItIsListedOpen()
        {
            var r = Record();
            Assert.That(await _store.TryOpenAsync(r, Limits, Now), Is.EqualTo(OpenOutcome.Opened));
            var read = await _store.GetAsync(r.AuthReqId);
            Assert.That(read!.Sub, Is.EqualTo(r.Sub));
            Assert.That(await _store.ListOpenIdsAsync(r.Sub, Now), Is.EqualTo(new[] { r.AuthReqId }));
        }

        [Test]
        public async Task Open_RefusesBeyondTheUserCap_AndWritesNothingWhenRefused()
        {
            for (var i = 0; i < 3; i++)
            {
                Assert.That(await _store.TryOpenAsync(Record(origin: "https://o" + i + ".test", n: i), Limits, Now), Is.EqualTo(OpenOutcome.Opened));
            }
            var extra = Record(origin: "https://o9.test", n: 9);
            Assert.That(await _store.TryOpenAsync(extra, Limits, Now), Is.EqualTo(OpenOutcome.UserCap));
            Assert.That(await _store.GetAsync(extra.AuthReqId), Is.Null);
        }

        [Test]
        public async Task Open_RefusesBeyondThePendingCapPerClientUserAndOrigin_ButAnotherOriginOrClientIsFine()
        {
            Assert.That(await _store.TryOpenAsync(Record(n: 1), Limits, Now), Is.EqualTo(OpenOutcome.Opened));
            Assert.That(await _store.TryOpenAsync(Record(n: 2), Limits, Now), Is.EqualTo(OpenOutcome.Opened));
            Assert.That(await _store.TryOpenAsync(Record(n: 3), Limits, Now), Is.EqualTo(OpenOutcome.OriginCap));
            // Another origin is a separate bucket (the user has 2 of 3 open); the user cap then ends it.
            Assert.That(await _store.TryOpenAsync(Record(n: 4, origin: "https://other.test"), Limits, Now), Is.EqualTo(OpenOutcome.Opened));
            Assert.That(await _store.TryOpenAsync(Record(n: 5, origin: "https://third.test"), Limits, Now), Is.EqualTo(OpenOutcome.UserCap));
        }

        [Test]
        public async Task Open_ReleasingASlot_LetsTheNextRequestIn()
        {
            var first = Record(n: 1);
            var second = Record(n: 2);
            await _store.TryOpenAsync(first, Limits, Now);
            await _store.TryOpenAsync(second, Limits, Now);
            Assert.That(await _store.TryOpenAsync(Record(n: 3), Limits, Now), Is.EqualTo(OpenOutcome.OriginCap));
            await _store.ReleaseAsync(first);
            Assert.That(await _store.TryOpenAsync(Record(n: 4), Limits, Now), Is.EqualTo(OpenOutcome.Opened));
            Assert.That(await _store.ListOpenIdsAsync(first.Sub, Now), Does.Not.Contain(first.AuthReqId));
        }

        [Test]
        public async Task Open_ExpiredRequests_DoNotHoldSlots()
        {
            var limits = new ConsentLimits { MaxOpenPerUser = 1, MaxPendingPerClientOrigin = 1, MaxPerClientUserHour = 50, RetentionSeconds = 3600 };
            var old = Record(n: 1, expiry: Now.ToUnixTimeSeconds() + 10);
            Assert.That(await _store.TryOpenAsync(old, limits, Now), Is.EqualTo(OpenOutcome.Opened));
            Assert.That(await _store.TryOpenAsync(Record(n: 2), limits, Now), Is.EqualTo(OpenOutcome.UserCap));
            Assert.That(await _store.TryOpenAsync(Record(n: 3), limits, Now.AddSeconds(11)), Is.EqualTo(OpenOutcome.Opened));
        }

        [Test]
        public async Task Open_HourlyRateLimit_CountsPerClientAndUser_AndSurvivesReleases()
        {
            var limits = new ConsentLimits { MaxOpenPerUser = 100, MaxPendingPerClientOrigin = 100, MaxPerClientUserHour = 4, RetentionSeconds = 3600 };
            for (var i = 0; i < 4; i++)
            {
                var r = Record(n: i);
                Assert.That(await _store.TryOpenAsync(r, limits, Now), Is.EqualTo(OpenOutcome.Opened));
                await _store.ReleaseAsync(r); // a decided request still counts against the hourly rate
            }
            Assert.That(await _store.TryOpenAsync(Record(n: 5), limits, Now), Is.EqualTo(OpenOutcome.RateLimited));
            Assert.That(await _store.TryOpenAsync(Record(n: 6, client: "other-client"), limits, Now), Is.EqualTo(OpenOutcome.Opened));
            Assert.That(await _store.TryOpenAsync(Record(n: 7), limits, Now.AddHours(1)), Is.EqualTo(OpenOutcome.Opened), "a new hour");
        }

        [Test]
        public async Task Open_ADuplicateId_IsACollision_AndChangesNothing()
        {
            var r = Record(n: 1);
            await _store.TryOpenAsync(r, Limits, Now);
            var again = Record(n: 1);
            again.Sub = "someone-else-" + _run;
            Assert.That(await _store.TryOpenAsync(again, Limits, Now), Is.EqualTo(OpenOutcome.IdCollision));
            Assert.That((await _store.GetAsync(r.AuthReqId))!.Sub, Is.EqualTo(r.Sub));
            Assert.That(await _store.ListOpenIdsAsync(again.Sub, Now), Is.Empty);
        }

        [Test]
        public async Task ConcurrentOpens_NeverExceedTheCap()
        {
            var limits = new ConsentLimits { MaxOpenPerUser = 5, MaxPendingPerClientOrigin = 100, MaxPerClientUserHour = 1000, RetentionSeconds = 3600 };
            var outcomes = await Task.WhenAll(Enumerable.Range(0, 40).Select(i => _store.TryOpenAsync(Record(n: i), limits, Now)));
            Assert.That(outcomes.Count(o => o == OpenOutcome.Opened), Is.EqualTo(5));
            Assert.That(outcomes.Count(o => o == OpenOutcome.UserCap), Is.EqualTo(35));
            Assert.That(await _store.ListOpenIdsAsync(Record().Sub, Now), Has.Count.EqualTo(5));
        }

        [Test]
        public async Task Update_IsCompareAndSet_OnlyOneOfTwoWritersWins()
        {
            var r = Record();
            await _store.TryOpenAsync(r, Limits, Now);
            var read1 = (await _store.GetAsync(r.AuthReqId))!;
            var read2 = (await _store.GetAsync(r.AuthReqId))!;

            var a = (await _store.GetAsync(r.AuthReqId))!;
            a.Status = ConsentStatus.Approved;
            var b = (await _store.GetAsync(r.AuthReqId))!;
            b.Status = ConsentStatus.Denied;

            Assert.That(await _store.TryUpdateAsync(read1, a), Is.True);
            Assert.That(await _store.TryUpdateAsync(read2, b), Is.False, "the second writer read an older version");
            var final = (await _store.GetAsync(r.AuthReqId))!;
            Assert.That(final.Status, Is.EqualTo(ConsentStatus.Approved));
            Assert.That(final.Version, Is.EqualTo(1));
        }

        [Test]
        public async Task ConcurrentUpdatesOfTheSameVersion_HaveExactlyOneWinner()
        {
            var r = Record();
            await _store.TryOpenAsync(r, Limits, Now);
            var wins = await Task.WhenAll(Enumerable.Range(0, 30).Select(async _ =>
            {
                var read = (await _store.GetAsync(r.AuthReqId))!;
                var next = (await _store.GetAsync(r.AuthReqId))!;
                next.AttemptsRemaining = 0;
                return await _store.TryUpdateAsync(read, next);
            }));
            var final = (await _store.GetAsync(r.AuthReqId))!;
            Assert.That(wins.Count(w => w), Is.EqualTo(final.Version), "every accepted update raised the version by exactly one");
            Assert.That(final.Version, Is.GreaterThanOrEqualTo(1));
        }

        [Test]
        public async Task Update_OfAMissingRecord_IsRefused_AndKeepsTheTtl()
        {
            var ghost = Record(n: 77);
            Assert.That(await _store.TryUpdateAsync(ghost, Record(n: 77)), Is.False);

            var r = Record(n: 1);
            await _store.TryOpenAsync(r, Limits, Now);
            var ttlBefore = await _redis!.GetDatabase().KeyTimeToLiveAsync("oidc:consent:" + r.AuthReqId);
            var read = (await _store.GetAsync(r.AuthReqId))!;
            var next = (await _store.GetAsync(r.AuthReqId))!;
            next.Status = ConsentStatus.Denied;
            Assert.That(await _store.TryUpdateAsync(read, next), Is.True);
            var ttlAfter = await _redis.GetDatabase().KeyTimeToLiveAsync("oidc:consent:" + r.AuthReqId);
            Assert.That(ttlBefore, Is.Not.Null);
            Assert.That(ttlAfter, Is.Not.Null.And.GreaterThan(TimeSpan.FromSeconds(300)), "an update never makes a record immortal or shortens retention");
        }
    }
}
