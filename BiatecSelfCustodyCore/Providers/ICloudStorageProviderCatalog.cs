namespace BiatecSelfCustodyCore.Providers
{
    /// <summary>Resolves registered <see cref="ICloudStorageProvider"/>s by name for provider-picker UIs and lookups.</summary>
    public interface ICloudStorageProviderCatalog
    {
        /// <summary>All registered providers, in DI registration order - what picker UIs render buttons from.</summary>
        IReadOnlyList<ICloudStorageProvider> All { get; }

        /// <summary>
        /// Resolves a provider by name (case-insensitive). Falls back to the configured default
        /// provider for a null/empty/unrecognized name, so data recorded before a given provider
        /// existed (or before any provider choice existed at all) keeps resolving the same way.
        /// </summary>
        ICloudStorageProvider Resolve(string? name);
    }
}
