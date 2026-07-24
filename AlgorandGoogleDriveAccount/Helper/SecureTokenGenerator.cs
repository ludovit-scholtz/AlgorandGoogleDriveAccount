using Microsoft.IdentityModel.Tokens;
using System.Security.Cryptography;

namespace AlgorandGoogleDriveAccount.Helper
{
    /// <summary>
    /// Generates cryptographically secure, URL-safe opaque tokens (e.g. session IDs, authorization codes,
    /// refresh tokens) using a CSPRNG. Shared by every component that needs an unguessable bearer identifier.
    /// </summary>
    public static class SecureTokenGenerator
    {
        /// <param name="size">Number of random bytes to generate (not the resulting string length).</param>
        public static string Generate(int size)
        {
            return Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(size));
        }
    }
}
