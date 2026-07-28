namespace BiatecSelfCustodyCore.Providers
{
    public class CloudStorageProviderCatalog : ICloudStorageProviderCatalog
    {
        /// <summary>
        /// Legacy default: sessions/tokens recorded before provider choice existed at all (or with
        /// an unrecognized name) resolve to Google, the only backend that existed then.
        /// </summary>
        private const string DefaultProviderName = "Google";

        public IReadOnlyList<ICloudStorageProvider> All { get; }

        public CloudStorageProviderCatalog(IEnumerable<ICloudStorageProvider> providers)
        {
            All = providers.ToList();
        }

        public ICloudStorageProvider Resolve(string? name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                var match = All.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    return match;
                }
            }

            var fallback = All.FirstOrDefault(p => string.Equals(p.Name, DefaultProviderName, StringComparison.OrdinalIgnoreCase))
                ?? All.FirstOrDefault();

            return fallback ?? throw new InvalidOperationException("No ICloudStorageProvider is registered.");
        }
    }
}
