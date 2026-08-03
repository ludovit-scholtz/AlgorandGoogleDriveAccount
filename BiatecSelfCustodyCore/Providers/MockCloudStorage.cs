using System.Collections.Concurrent;

namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>
    /// In-memory file store backing <see cref="MockCloudStorageProvider"/> - test/dev tooling only, never
    /// used in production (see <c>BiatecOIDC/MOCK_TESTING.md</c>). Registered as a singleton so its
    /// contents live for the process's lifetime (reset on every restart, by design - see
    /// <see cref="Repository.ICloudAccountRepository.SeedTestVaultAsync"/> for how the configured mock
    /// accounts are deterministically re-seeded into it every time the app starts).
    /// </summary>
    public sealed class MockCloudStorage
    {
        private readonly ConcurrentDictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

        public byte[]? TryGet(string email, string fileName) =>
            _files.TryGetValue(Key(email, fileName), out var bytes) ? bytes : null;

        public void Set(string email, string fileName, byte[] content) =>
            _files[Key(email, fileName)] = content;

        public void Delete(string email, string fileName) =>
            _files.TryRemove(Key(email, fileName), out _);

        private static string Key(string email, string fileName) => $"{email}::{fileName}";
    }
}
