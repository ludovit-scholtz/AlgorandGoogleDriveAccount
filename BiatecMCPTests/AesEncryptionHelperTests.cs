using BiatecSelfCustodyCore.Helper;
using System.Security.Cryptography;
using System.Text;

namespace BiatecMCPTests
{
    [TestFixture]
    public class AesEncryptionHelperTests
    {
        private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);
        private static readonly byte[] Iv = RandomNumberGenerator.GetBytes(16);
        private const string Email = "user@example.com";

        /// <summary>
        /// Independent re-implementation of the pre-versioning (legacy) encryption scheme - a single
        /// unsalted SHA-256 hash of <c>baseValue||email</c> for the AES-CBC key/IV, no header, no auth tag -
        /// used to build a fixture that proves already-encrypted files keep decrypting after the AES-GCM
        /// upgrade, without depending on any internals of <see cref="AesEncryptionHelper"/> itself.
        /// </summary>
        private static byte[] EncryptLegacy(byte[] plaintext, byte[] key, byte[] iv, string email)
        {
            static byte[] DeriveKey(byte[] baseValue, string email, int length)
            {
                using var sha256 = SHA256.Create();
                var combined = baseValue.Concat(Encoding.UTF8.GetBytes(email)).ToArray();
                return sha256.ComputeHash(combined).Take(length).ToArray();
            }

            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = DeriveKey(key, email, 32);
            aes.IV = DeriveKey(iv, email, 16);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var encryptor = aes.CreateEncryptor();
            using var ms = new MemoryStream();
            using (var cs = new CryptoStream(ms, encryptor, CryptoStreamMode.Write))
            {
                cs.Write(plaintext, 0, plaintext.Length);
                cs.FlushFinalBlock();
            }
            return ms.ToArray();
        }

        [Test]
        public void Encrypt_ThenDecrypt_RoundTripsPlaintext()
        {
            var plaintext = Encoding.UTF8.GetBytes("this is a test algorand mnemonic phrase");

            var ciphertext = AesEncryptionHelper.Encrypt(plaintext, Key, Iv, Email);
            var decrypted = AesEncryptionHelper.Decrypt(ciphertext, Key, Iv, Email);

            Assert.That(decrypted, Is.EqualTo(plaintext));
        }

        [Test]
        public void Encrypt_ProducesDifferentCiphertextEachCall()
        {
            var plaintext = Encoding.UTF8.GetBytes("same plaintext every time");

            var ciphertext1 = AesEncryptionHelper.Encrypt(plaintext, Key, Iv, Email);
            var ciphertext2 = AesEncryptionHelper.Encrypt(plaintext, Key, Iv, Email);

            // Random per-file salt/nonce means encrypting the same plaintext twice must not produce
            // identical ciphertext (rules out IV/key reuse across writes).
            Assert.That(ciphertext1, Is.Not.EqualTo(ciphertext2));
        }

        [Test]
        public void Decrypt_LegacyFormatFixture_StillDecryptsCorrectly()
        {
            var plaintext = Encoding.UTF8.GetBytes("legacy mnemonic encrypted before the AES-GCM upgrade");
            var legacyCiphertext = EncryptLegacy(plaintext, Key, Iv, Email);

            var decrypted = AesEncryptionHelper.Decrypt(legacyCiphertext, Key, Iv, Email);

            Assert.That(decrypted, Is.EqualTo(plaintext));
        }

        [Test]
        public void Decrypt_TamperedCurrentFormatCiphertext_Throws()
        {
            var plaintext = Encoding.UTF8.GetBytes("tamper detection test");
            var ciphertext = AesEncryptionHelper.Encrypt(plaintext, Key, Iv, Email);

            // Flip a byte inside the ciphertext region (after the magic/salt/nonce header).
            ciphertext[^1] ^= 0xFF;

            Assert.Throws<AuthenticationTagMismatchException>(() =>
                AesEncryptionHelper.Decrypt(ciphertext, Key, Iv, Email));
        }

        [Test]
        public void Decrypt_CurrentFormat_WrongEmail_Throws()
        {
            var plaintext = Encoding.UTF8.GetBytes("bound to the pairing email");
            var ciphertext = AesEncryptionHelper.Encrypt(plaintext, Key, Iv, Email);

            Assert.Throws<AuthenticationTagMismatchException>(() =>
                AesEncryptionHelper.Decrypt(ciphertext, Key, Iv, "someone-else@example.com"));
        }

        [Test]
        public void Decrypt_LegacyFormat_WrongEmail_ThrowsOrProducesGarbage()
        {
            var plaintext = Encoding.UTF8.GetBytes("legacy mnemonic");
            var legacyCiphertext = EncryptLegacy(plaintext, Key, Iv, Email);

            // Legacy CBC has no authentication tag, so a wrong email either throws (padding failure) or
            // decrypts to garbage bytes that don't match the original plaintext - it must never silently
            // reproduce the correct plaintext for the wrong email.
            byte[]? decrypted = null;
            try
            {
                decrypted = AesEncryptionHelper.Decrypt(legacyCiphertext, Key, Iv, "someone-else@example.com");
            }
            catch (CryptographicException)
            {
                // Acceptable outcome for the legacy (unauthenticated) format.
            }

            if (decrypted != null)
            {
                Assert.That(decrypted, Is.Not.EqualTo(plaintext));
            }
        }
    }
}
