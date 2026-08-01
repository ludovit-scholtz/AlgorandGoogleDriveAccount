using BiatecSelfCustodyCore.Providers;

namespace BiatecMCPTests
{
    /// <summary>Minimal <see cref="ICloudStorageProvider"/> test double for tests that just need a resolvable provider name.</summary>
    internal sealed class FakeCloudStorageProvider : ICloudStorageProvider
    {
        public FakeCloudStorageProvider(string name = "Google", bool isConfigured = true)
        {
            Name = name;
            IsConfigured = isConfigured;
        }
        public string Name { get; }
        public string DisplayName => Name;
        public string RequiredScope => "fake-scope";
        public bool IsConfigured { get; }
        public Task<byte[]?> TryDownloadAsync(string fileName, string accessToken) => Task.FromResult<byte[]?>(null);
        public Task UploadAsync(string fileName, byte[] content, string accessToken) => Task.CompletedTask;
        public Task<bool> HasWriteAccessAsync(string accessToken) => Task.FromResult(true);
        public Task<string?> GetAmbientAccessTokenAsync() => Task.FromResult<string?>(null);
        public Task<string?> GetAmbientRefreshTokenAsync() => Task.FromResult<string?>(null);
        public Task<ProviderTokenRefreshResult?> RefreshAccessTokenAsync(string refreshToken) => Task.FromResult<ProviderTokenRefreshResult?>(null);
    }
}
