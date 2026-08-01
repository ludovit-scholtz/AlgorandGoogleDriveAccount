using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;

namespace BiatecOIDCTests
{
    [TestFixture]
    public class ProviderAccessTokenProtectorTests
    {
        private const string TestEmail = "user@example.com";

        private Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>> _mockConfig = null!;
        private ProviderTokenProtectionConfiguration _config = null!;
        private ProviderAccessTokenProtector _protector = null!;

        [SetUp]
        public void SetUp()
        {
            _config = new ProviderTokenProtectionConfiguration
            {
                Key = Convert.ToBase64String(new byte[32]),
                IV = Convert.ToBase64String(new byte[16])
            };
            _mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            _mockConfig.Setup(c => c.CurrentValue).Returns(() => _config);

            _protector = CreateProtector(_mockConfig.Object, Environments.Development);
        }

        private static ProviderAccessTokenProtector CreateProtector(IOptionsMonitor<ProviderTokenProtectionConfiguration> config, string environmentName)
        {
            var mockEnvironment = new Mock<IHostEnvironment>();
            mockEnvironment.Setup(e => e.EnvironmentName).Returns(environmentName);
            return new ProviderAccessTokenProtector(config, mockEnvironment.Object, new Mock<ILogger<ProviderAccessTokenProtector>>().Object);
        }

        [Test]
        public void Protect_ThenUnprotect_RoundTripsTheOriginalToken()
        {
            var protectedToken = _protector.Protect("ya29.a0-real-google-token", TestEmail);

            Assert.That(protectedToken, Is.Not.Null.And.Not.Empty);
            var result = _protector.Unprotect(protectedToken, TestEmail);

            Assert.That(result, Is.EqualTo("ya29.a0-real-google-token"));
        }

        [Test]
        public void Protect_NeverReturnsThePlaintextToken()
        {
            const string plaintext = "super-secret-provider-token";

            var protectedToken = _protector.Protect(plaintext, TestEmail);

            Assert.That(protectedToken, Does.Not.Contain(plaintext));
        }

        [Test]
        public void Unprotect_WithDifferentEmail_ReturnsNull()
        {
            var protectedToken = _protector.Protect("token-for-alice", "alice@example.com");

            var result = _protector.Unprotect(protectedToken, "bob@example.com");

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Unprotect_TamperedCiphertext_ReturnsNull()
        {
            var protectedToken = _protector.Protect("some-token", TestEmail)!;
            var bytes = Convert.FromBase64String(protectedToken);
            bytes[^1] ^= 0xFF; // flip a bit near the authentication tag
            var tampered = Convert.ToBase64String(bytes);

            var result = _protector.Unprotect(tampered, TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Unprotect_NullOrBlankInput_ReturnsNull()
        {
            Assert.That(_protector.Unprotect(null, TestEmail), Is.Null);
            Assert.That(_protector.Unprotect(string.Empty, TestEmail), Is.Null);
            Assert.That(_protector.Unprotect("   ", TestEmail), Is.Null);
        }

        [Test]
        public void Protect_BlankToken_ReturnsNull()
        {
            Assert.That(_protector.Protect(string.Empty, TestEmail), Is.Null);
        }

        [Test]
        public void Protect_KeyNotConfigured_ReturnsNullInsteadOfThrowing()
        {
            _config.Key = string.Empty;
            _config.IV = string.Empty;

            var result = _protector.Protect("some-token", TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Unprotect_KeyNotConfigured_ReturnsNullInsteadOfThrowing()
        {
            var protectedToken = _protector.Protect("some-token", TestEmail);
            _config.Key = string.Empty;
            _config.IV = string.Empty;

            var result = _protector.Unprotect(protectedToken, TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Protect_InvalidBase64Key_ReturnsNullInsteadOfThrowing()
        {
            _config.Key = "not-valid-base64!!!";

            var result = _protector.Protect("some-token", TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Protect_WrongKeyLength_ReturnsNullInsteadOfThrowing()
        {
            _config.Key = Convert.ToBase64String(new byte[16]); // should be 32 bytes

            var result = _protector.Protect("some-token", TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void DifferentKeys_ProduceDifferentCiphertextForSameInput()
        {
            var protectedWithKey1 = _protector.Protect("same-token", TestEmail);

            _config.Key = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
            var protectedWithKey2 = _protector.Protect("same-token", TestEmail);

            Assert.That(protectedWithKey1, Is.Not.EqualTo(protectedWithKey2));
        }

        // ───────────────────────── Fail-fast construction (production only) ─────────────────────────
        // No wallet endpoint accepts a caller-supplied provider token anymore, so a missing/invalid key
        // means the wallet API can't function at all - this should be surfaced loudly outside
        // Development, not discovered one 401 at a time.

        [Test]
        public void Construction_KeyMissing_InProduction_Throws()
        {
            var config = new ProviderTokenProtectionConfiguration { Key = string.Empty, IV = string.Empty };
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.Throws<InvalidOperationException>(() => CreateProtector(mockConfig.Object, Environments.Production));
        }

        [Test]
        public void Construction_KeyInvalid_InProduction_Throws()
        {
            var config = new ProviderTokenProtectionConfiguration { Key = "not-valid-base64!!!", IV = Convert.ToBase64String(new byte[16]) };
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.Throws<InvalidOperationException>(() => CreateProtector(mockConfig.Object, Environments.Production));
        }

        [Test]
        public void Construction_KeyMissing_InDevelopment_DoesNotThrow()
        {
            var config = new ProviderTokenProtectionConfiguration { Key = string.Empty, IV = string.Empty };
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.DoesNotThrow(() => CreateProtector(mockConfig.Object, Environments.Development));
        }

        [Test]
        public void Construction_KeyValid_InProduction_DoesNotThrow()
        {
            var config = new ProviderTokenProtectionConfiguration
            {
                Key = Convert.ToBase64String(new byte[32]),
                IV = Convert.ToBase64String(new byte[16])
            };
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.DoesNotThrow(() => CreateProtector(mockConfig.Object, Environments.Production));
        }
    }
}
