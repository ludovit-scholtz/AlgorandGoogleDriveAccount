using System.Security.Cryptography;
using System.Text;

namespace BiatecSelfCustodyCore.Helper
{
    /// <summary>
    /// Encrypts/decrypts the Algorand mnemonic stored in a user's Google Drive file.
    /// </summary>
    /// <remarks>
    /// Writes a versioned, authenticated format (see <see cref="FormatMagic"/>): a per-file random salt feeds
    /// a real per-file key derivation (HKDF) instead of hashing the shared configuration secret with the email
    /// alone, and AES-256-GCM provides tamper detection instead of unauthenticated CBC. <see cref="Decrypt"/>
    /// still recognizes and decrypts the legacy unversioned CBC format (single SHA-256 hash of
    /// <c>baseValue||email</c>, no salt, no auth tag) so every file already encrypted under the old scheme
    /// before this change shipped keeps working with no migration step - only newly created files use the new
    /// format.
    /// </remarks>
    public static class AesEncryptionHelper
    {
        // 8-byte marker prefixed to every file written under the new format. Legacy files are raw CBC
        // ciphertext with no header, so this can never occur at the start of the ciphertext with a chance any
        // higher than an 8-byte hash collision (~1 in 2^64) — for that to matter, an existing legacy CBC
        // ciphertext would have to itself start with these exact bytes, which is cryptographically negligible.
        private static readonly byte[] FormatMagic = Encoding.ASCII.GetBytes("BIATECV2");
        private const int SaltSize = 16;
        private const int NonceSize = 12;
        private const int TagSize = 16;

        /// <summary>
        /// Encrypts <paramref name="data"/> using the current (versioned, authenticated) format: a random
        /// per-file salt drives an HKDF-derived per-file AES-256 key, and AES-GCM provides both confidentiality
        /// and integrity.
        /// </summary>
        public static byte[] Encrypt(byte[] data, byte[] key, byte[] iv, string email)
        {
            var salt = RandomNumberGenerator.GetBytes(SaltSize);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var derivedKey = DeriveKeyV2(key, email, salt);

            var ciphertext = new byte[data.Length];
            var tag = new byte[TagSize];

            using (var aesGcm = new AesGcm(derivedKey, TagSize))
            {
                aesGcm.Encrypt(nonce, data, ciphertext, tag);
            }

            var output = new byte[FormatMagic.Length + salt.Length + nonce.Length + ciphertext.Length + tag.Length];
            var offset = 0;
            Buffer.BlockCopy(FormatMagic, 0, output, offset, FormatMagic.Length); offset += FormatMagic.Length;
            Buffer.BlockCopy(salt, 0, output, offset, salt.Length); offset += salt.Length;
            Buffer.BlockCopy(nonce, 0, output, offset, nonce.Length); offset += nonce.Length;
            Buffer.BlockCopy(ciphertext, 0, output, offset, ciphertext.Length); offset += ciphertext.Length;
            Buffer.BlockCopy(tag, 0, output, offset, tag.Length);
            return output;
        }

        /// <summary>
        /// Decrypts <paramref name="cipherData"/>. Detects the current versioned AES-GCM format via its magic
        /// prefix; falls back to the legacy unversioned AES-CBC format (unchanged behavior) otherwise, so
        /// already-encrypted files keep working.
        /// </summary>
        public static byte[] Decrypt(byte[] cipherData, byte[] key, byte[] iv, string email)
        {
            if (IsCurrentFormat(cipherData))
            {
                return DecryptV2(cipherData, key, email);
            }

            return DecryptLegacy(cipherData, key, iv, email);
        }

        private static bool IsCurrentFormat(byte[] cipherData)
        {
            if (cipherData.Length < FormatMagic.Length + SaltSize + NonceSize + TagSize)
            {
                return false;
            }

            for (var i = 0; i < FormatMagic.Length; i++)
            {
                if (cipherData[i] != FormatMagic[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static byte[] DecryptV2(byte[] cipherData, byte[] key, string email)
        {
            var offset = FormatMagic.Length;
            var salt = cipherData[offset..(offset + SaltSize)]; offset += SaltSize;
            var nonce = cipherData[offset..(offset + NonceSize)]; offset += NonceSize;
            var ciphertextLength = cipherData.Length - offset - TagSize;
            var ciphertext = cipherData[offset..(offset + ciphertextLength)]; offset += ciphertextLength;
            var tag = cipherData[offset..(offset + TagSize)];

            var derivedKey = DeriveKeyV2(key, email, salt);
            var plaintext = new byte[ciphertextLength];

            using var aesGcm = new AesGcm(derivedKey, TagSize);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        /// <summary>
        /// Real per-file key derivation: HKDF-SHA256 over the shared configured key material, salted per
        /// file and bound to the email, replacing the legacy single unsalted SHA-256 hash.
        /// </summary>
        private static byte[] DeriveKeyV2(byte[] baseValue, string email, byte[] salt)
        {
            return HKDF.DeriveKey(HashAlgorithmName.SHA256, baseValue, outputLength: 32, salt: salt, info: Encoding.UTF8.GetBytes(email));
        }

        private static byte[] DecryptLegacy(byte[] cipherData, byte[] key, byte[] iv, string email)
        {
            using var aes = Aes.Create();
            aes.KeySize = 256;
            aes.Key = DeriveKeyLegacy(key, email, 32);
            aes.IV = DeriveKeyLegacy(iv, email, 16);
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;

            using var decryptor = aes.CreateDecryptor();
            return PerformCryptography(cipherData, decryptor);
        }

        private static byte[] PerformCryptography(byte[] data, ICryptoTransform transform)
        {
            using var ms = new MemoryStream();
            using var cryptoStream = new CryptoStream(ms, transform, CryptoStreamMode.Write);
            cryptoStream.Write(data, 0, data.Length);
            cryptoStream.FlushFinalBlock();
            return ms.ToArray();
        }

        /// <summary>
        /// Legacy (pre-versioning) key derivation kept only to decrypt files written before this change:
        /// a single, unsalted SHA-256 hash of the shared config secret concatenated with the email, truncated
        /// to the required length.
        /// </summary>
        private static byte[] DeriveKeyLegacy(byte[] baseValue, string email, int length)
        {
            using var sha256 = SHA256.Create();
            var combined = baseValue.Concat(Encoding.UTF8.GetBytes(email)).ToArray();
            var hash = sha256.ComputeHash(combined);
            return hash.Take(length).ToArray();
        }

        /// <summary>
        /// Generates a unique identifier for one generation of AES key material - a short hash used as the
        /// <c>%AESID%</c> placeholder in encrypted file names, so each key generation's files live under a
        /// distinct name (see <c>EncryptedKeyRingFileStore</c>: this is what lets rotation locate which
        /// generation encrypted an existing file without needing to try decrypting it under several keys).
        /// </summary>
        /// <remarks>The method computes a SHA-256 hash of the concatenated key and IV, and returns the
        /// first 6 bytes of the hash as a lowercase hexadecimal string.</remarks>
        /// <param name="key">32-byte AES-256 key.</param>
        /// <param name="iv">16-byte AES initialization vector.</param>
        /// <returns>A 6-byte hexadecimal string derived from the key and IV.</returns>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="key"/> or <paramref name="iv"/> is null.</exception>
        /// <exception cref="ArgumentException">Thrown if the key length is not 32 bytes or the IV length is not 16 bytes.</exception>
        public static string MakeAesId(byte[] key, byte[] iv)
        {
            if (key == null || iv == null)
            {
                throw new ArgumentNullException("Key and IV cannot be null");
            }
            if (key.Length != 32 || iv.Length != 16)
            {
                throw new ArgumentException("Invalid key or IV length");
            }
            // hash the key and IV together to create a unique identifier
            // output is a hex string of length 6 bytes
            using var sha256 = SHA256.Create();
            var combined = key.Concat(iv).ToArray();
            var hash = sha256.ComputeHash(combined);
            return BitConverter.ToString(hash.Take(6).ToArray()).Replace("-", "").ToLowerInvariant();

        }
    }
}
