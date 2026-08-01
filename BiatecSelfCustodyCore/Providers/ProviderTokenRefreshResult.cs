namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// The result of successfully exchanging a cached provider refresh token for a fresh access token
    /// (see <see cref="ICloudStorageProvider.RefreshAccessTokenAsync"/>).
    /// </summary>
    /// <param name="AccessToken">The fresh access token.</param>
    /// <param name="RefreshToken">
    /// A rotated refresh token, if the provider issued one (Microsoft Entra ID always rotates; Google
    /// normally doesn't). <c>null</c> means the original refresh token the caller supplied is still valid
    /// and should keep being used.
    /// </param>
    public sealed record ProviderTokenRefreshResult(string AccessToken, string? RefreshToken);
}
