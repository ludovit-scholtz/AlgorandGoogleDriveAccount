using BiatecSelfCustodyCore.Providers;

namespace BiatecOIDCTests
{
    /// <summary>
    /// In-memory <see cref="ICloudStorageProvider"/> test double - actually persists uploaded bytes across
    /// calls within a test (unlike a no-op fake), so round-trip encryption/decryption tests
    /// (<c>SpendingLimitServiceTests</c>) can verify real save-then-load behavior.
    /// </summary>
    internal sealed class FakeCloudStorageProvider : ICloudStorageProvider
    {
        private readonly Dictionary<string, byte[]> _files = new();
        private readonly string? _ambientAccessToken;

        public FakeCloudStorageProvider(string name = "Fake", string? ambientAccessToken = "ambient-token")
        {
            Name = name;
            _ambientAccessToken = ambientAccessToken;
        }

        public string Name { get; }
        public string DisplayName => Name;
        public string RequiredScope => "fake-scope";
        public bool IsConfigured => true;

        public Task<byte[]?> TryDownloadAsync(string fileName, string accessToken)
        {
            return Task.FromResult(_files.TryGetValue(fileName, out var content) ? content : null);
        }

        public Task UploadAsync(string fileName, byte[] content, string accessToken)
        {
            _files[fileName] = content;
            return Task.CompletedTask;
        }

        public Task<bool> HasWriteAccessAsync(string accessToken) => Task.FromResult(true);

        public Task<string?> GetAmbientAccessTokenAsync() => Task.FromResult(_ambientAccessToken);
    }
}
