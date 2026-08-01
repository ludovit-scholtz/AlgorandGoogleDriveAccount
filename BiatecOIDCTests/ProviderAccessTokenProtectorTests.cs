using BiatecOIDC.BusinessLogic;
using BiatecOIDC.Model;
using BiatecSelfCustodyCore.Model;
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
        private const string ActiveKeyId = "gen-1";

        private Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>> _mockConfig = null!;
        private ProviderTokenProtectionConfiguration _config = null!;
        private ProviderAccessTokenProtector _protector = null!;

        [SetUp]
        public void SetUp()
        {
            _config = BuildConfig(ActiveKeyId, MakeEntry(ActiveKeyId, 1));
            _mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            _mockConfig.Setup(c => c.CurrentValue).Returns(() => _config);

            _protector = CreateProtector(_mockConfig.Object, Environments.Development);
        }

        private static AesKeyRingEntry MakeEntry(string keyId, byte fill) => new()
        {
            KeyId = keyId,
            Key = Convert.ToBase64String(Enumerable.Repeat(fill, 32).ToArray()),
            IV = Convert.ToBase64String(Enumerable.Repeat(fill, 16).ToArray())
        };

        private static ProviderTokenProtectionConfiguration BuildConfig(string activeKeyId, params AesKeyRingEntry[] keys) => new()
        {
            ActiveKeyId = activeKeyId,
            Keys = keys.ToList()
        };

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
        public void Protect_ActiveKeyNotConfigured_ReturnsNullInsteadOfThrowing()
        {
            _config.ActiveKeyId = string.Empty;

            var result = _protector.Protect("some-token", TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Unprotect_ActiveKeyRemovedEntirely_ReturnsNullInsteadOfThrowing()
        {
            var protectedToken = _protector.Protect("some-token", TestEmail);
            _config.Keys.Clear();

            var result = _protector.Unprotect(protectedToken, TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Protect_InvalidBase64Key_ReturnsNullInsteadOfThrowing()
        {
            _config.Keys[0].Key = "not-valid-base64!!!";

            var result = _protector.Protect("some-token", TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void Protect_WrongKeyLength_ReturnsNullInsteadOfThrowing()
        {
            _config.Keys[0].Key = Convert.ToBase64String(new byte[16]); // should be 32 bytes

            var result = _protector.Protect("some-token", TestEmail);

            Assert.That(result, Is.Null);
        }

        [Test]
        public void DifferentKeys_ProduceDifferentCiphertextForSameInput()
        {
            var protectedWithKey1 = _protector.Protect("same-token", TestEmail);

            _config.Keys[0].Key = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray());
            var protectedWithKey2 = _protector.Protect("same-token", TestEmail);

            Assert.That(protectedWithKey1, Is.Not.EqualTo(protectedWithKey2));
        }

        // ───────────────────────── Key rotation ─────────────────────────

        [Test]
        public void Unprotect_TokenEncryptedUnderRetiredKey_StillDecryptsViaHistoricalKey()
        {
            var protectedToken = _protector.Protect("token-from-before-rotation", TestEmail);

            // Rotate: new active generation, old one demoted to historical (still present).
            _config = BuildConfig("gen-2", MakeEntry("gen-2", 2), MakeEntry(ActiveKeyId, 1));

            var result = _protector.Unprotect(protectedToken, TestEmail);

            Assert.That(result, Is.EqualTo("token-from-before-rotation"));
        }

        [Test]
        public void Protect_AfterRotation_UsesTheNewActiveKey()
        {
            var protectedBeforeRotation = _protector.Protect("same-token", TestEmail);

            _config = BuildConfig("gen-2", MakeEntry("gen-2", 2), MakeEntry(ActiveKeyId, 1));
            var protectedAfterRotation = _protector.Protect("same-token", TestEmail);

            Assert.That(protectedAfterRotation, Is.Not.EqualTo(protectedBeforeRotation));
            // Round-trips under the new active key alone (no historical fallback needed for freshly-protected data).
            Assert.That(_protector.Unprotect(protectedAfterRotation, TestEmail), Is.EqualTo("same-token"));
        }

        [Test]
        public void Unprotect_TokenEncryptedUnderKeyThatWasFullyRemoved_ReturnsNullInsteadOfThrowing()
        {
            var protectedToken = _protector.Protect("token-from-a-retired-key", TestEmail);

            // The generation that encrypted this token is no longer configured at all (fully retired).
            _config = BuildConfig("gen-2", MakeEntry("gen-2", 2));

            var result = _protector.Unprotect(protectedToken, TestEmail);

            Assert.That(result, Is.Null);
        }

        // ───────────────────────── Fail-fast construction (production only) ─────────────────────────
        // No wallet endpoint accepts a caller-supplied provider token anymore, so a missing/invalid key
        // means the wallet API can't function at all - this should be surfaced loudly outside
        // Development, not discovered one 401 at a time.

        [Test]
        public void Construction_ActiveKeyMissing_InProduction_Throws()
        {
            var config = BuildConfig(string.Empty);
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.Throws<InvalidOperationException>(() => CreateProtector(mockConfig.Object, Environments.Production));
        }

        [Test]
        public void Construction_ActiveKeyInvalid_InProduction_Throws()
        {
            var config = BuildConfig(ActiveKeyId, new AesKeyRingEntry { KeyId = ActiveKeyId, Key = "not-valid-base64!!!", IV = Convert.ToBase64String(new byte[16]) });
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.Throws<InvalidOperationException>(() => CreateProtector(mockConfig.Object, Environments.Production));
        }

        [Test]
        public void Construction_ActiveKeyMissing_InDevelopment_DoesNotThrow()
        {
            var config = BuildConfig(string.Empty);
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.DoesNotThrow(() => CreateProtector(mockConfig.Object, Environments.Development));
        }

        [Test]
        public void Construction_ActiveKeyValid_InProduction_DoesNotThrow()
        {
            var config = BuildConfig(ActiveKeyId, MakeEntry(ActiveKeyId, 1));
            var mockConfig = new Mock<IOptionsMonitor<ProviderTokenProtectionConfiguration>>();
            mockConfig.Setup(c => c.CurrentValue).Returns(config);

            Assert.DoesNotThrow(() => CreateProtector(mockConfig.Object, Environments.Production));
        }
    }
}
