using BiatecSelfCustodyCore.Providers;

namespace BiatecMCPTests
{
    [TestFixture]
    public class CloudStorageProviderCatalogTests
    {
        private readonly FakeCloudStorageProvider _google = new("Google");
        private readonly FakeCloudStorageProvider _microsoft = new("Microsoft");

        private CloudStorageProviderCatalog CreateCatalog() => new(new ICloudStorageProvider[] { _google, _microsoft });

        [TestCase("Google")]
        [TestCase("google")]
        [TestCase("GOOGLE")]
        public void Resolve_MatchesByNameCaseInsensitively(string name)
        {
            var catalog = CreateCatalog();

            Assert.That(catalog.Resolve(name), Is.SameAs(_google));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        [TestCase("dropbox")]
        public void Resolve_MissingOrUnrecognizedName_FallsBackToGoogle(string? name)
        {
            // Sessions/tokens recorded before a given provider existed (or before any provider
            // choice existed at all) have no name recorded - they must keep resolving to Google,
            // the only backend that existed then, with no migration required.
            var catalog = CreateCatalog();

            Assert.That(catalog.Resolve(name), Is.SameAs(_google));
        }

        [Test]
        public void Resolve_UnrecognizedName_WhenGoogleNotRegistered_FallsBackToFirstRegistered()
        {
            var catalog = new CloudStorageProviderCatalog(new ICloudStorageProvider[] { _microsoft });

            Assert.That(catalog.Resolve("unknown"), Is.SameAs(_microsoft));
        }

        [Test]
        public void Resolve_NoProvidersRegistered_Throws()
        {
            var catalog = new CloudStorageProviderCatalog(Array.Empty<ICloudStorageProvider>());

            Assert.That(() => catalog.Resolve("Google"), Throws.InvalidOperationException);
        }

        [Test]
        public void All_ReturnsEveryRegisteredProvider()
        {
            var catalog = CreateCatalog();

            Assert.That(catalog.All, Is.EquivalentTo(new ICloudStorageProvider[] { _google, _microsoft }));
        }
    }
}
