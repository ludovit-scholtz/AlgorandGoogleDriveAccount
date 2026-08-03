using BiatecOIDC.BusinessLogic;
using BiatecSelfCustodyCore.Model;
using BiatecSelfCustodyCore.Providers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class AddressActivationServiceTests
    {
        private const string TestEmail = "user@example.com";
        private const string TestProvider = "Fake";

        private FakeCloudStorageProvider _fakeProvider = null!;
        private Mock<ICloudStorageProviderCatalog> _mockCatalog = null!;
        private AddressActivationService _service = null!;
        private AesOptions _aesOptionsValue = null!;

        private static AesKeyRingEntry MakeKeyEntry(string keyId, byte fill) => new()
        {
            KeyId = keyId,
            Key = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()),
            IV = Convert.ToBase64String(Enumerable.Repeat(fill, 16).ToArray())
        };

        private static AesOptions BuildAesOptions(string activeKeyId, params AesKeyRingEntry[] keys) => new()
        {
            ActiveKeyId = activeKeyId,
            Keys = keys.ToList()
        };

        [SetUp]
        public void SetUp()
        {
            _fakeProvider = new FakeCloudStorageProvider();
            _mockCatalog = new Mock<ICloudStorageProviderCatalog>();
            _mockCatalog.Setup(c => c.Resolve(It.IsAny<string>())).Returns(_fakeProvider);

            var aesOptions = new Mock<IOptionsMonitor<AesOptions>>();
            _aesOptionsValue = BuildAesOptions("gen-1", MakeKeyEntry("gen-1", 1));
            aesOptions.Setup(a => a.CurrentValue).Returns(() => _aesOptionsValue);

            var mockEnvironment = new Mock<IHostEnvironment>();
            mockEnvironment.Setup(e => e.EnvironmentName).Returns(Environments.Development);

            _service = new AddressActivationService(_mockCatalog.Object, aesOptions.Object, mockEnvironment.Object, new Mock<ILogger<AddressActivationService>>().Object);
        }

        [Test]
        public async Task TryResolveAsync_NeverActivated_ReturnsNull()
        {
            var result = await _service.TryResolveAsync(TestEmail, TestProvider, "token", "UNKNOWNADDR");

            Assert.That(result, Is.Null);
        }

        [Test]
        public async Task ActivateAsync_ThenTryResolveAsync_RoundTrips()
        {
            await _service.ActivateAsync(TestEmail, TestProvider, "token", "ADDR1", "Avm", "SEED1", 3);

            var resolved = await _service.TryResolveAsync(TestEmail, TestProvider, "token", "ADDR1");

            Assert.That(resolved, Is.Not.Null);
            Assert.That(resolved!.Family, Is.EqualTo("Avm"));
            Assert.That(resolved.PrimaryAddress, Is.EqualTo("SEED1"));
            Assert.That(resolved.Slot, Is.EqualTo(3));
        }

        [Test]
        public async Task ActivateAsync_ReactivatingSameAddress_OverwritesThePreviousEntry()
        {
            await _service.ActivateAsync(TestEmail, TestProvider, "token", "ADDR1", "Avm", "SEED1", 0);
            await _service.ActivateAsync(TestEmail, TestProvider, "token", "ADDR1", "Avm", "SEED2", 5);

            var resolved = await _service.TryResolveAsync(TestEmail, TestProvider, "token", "ADDR1");

            Assert.That(resolved!.PrimaryAddress, Is.EqualTo("SEED2"));
            Assert.That(resolved.Slot, Is.EqualTo(5));
            var all = await _service.ListAsync(TestEmail, TestProvider, "token");
            Assert.That(all, Has.Count.EqualTo(1));
        }

        [Test]
        public async Task ListAsync_MultipleActivatedAddresses_ReturnsAll()
        {
            await _service.ActivateAsync(TestEmail, TestProvider, "token", "ADDR1", "Avm", "SEED1", 0);
            await _service.ActivateAsync(TestEmail, TestProvider, "token", "0xEVM1", "Evm", "SEED1", 0);

            var all = await _service.ListAsync(TestEmail, TestProvider, "token");

            Assert.That(all, Has.Count.EqualTo(2));
        }

        [Test]
        public async Task ActivateAsync_PersistsAcrossServiceInstances()
        {
            await _service.ActivateAsync(TestEmail, TestProvider, "token", "ADDR1", "Avm", "SEED1", 0);

            var aesOptions = new Mock<IOptionsMonitor<AesOptions>>();
            aesOptions.Setup(a => a.CurrentValue).Returns(() => _aesOptionsValue);
            var mockEnvironment = new Mock<IHostEnvironment>();
            mockEnvironment.Setup(e => e.EnvironmentName).Returns(Environments.Development);
            var secondService = new AddressActivationService(_mockCatalog.Object, aesOptions.Object, mockEnvironment.Object, new Mock<ILogger<AddressActivationService>>().Object);

            var resolved = await secondService.TryResolveAsync(TestEmail, TestProvider, "token", "ADDR1");

            Assert.That(resolved, Is.Not.Null);
        }
    }
}
