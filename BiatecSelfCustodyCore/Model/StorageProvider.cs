namespace BiatecSelfCustodyCore.Model
{
    /// <summary>Which cloud identity/storage backend a user's self-custody account is bound to.</summary>
    public enum StorageProvider
    {
        /// <summary>Google sign-in, account file stored in the user's Google Drive.</summary>
        Google,

        /// <summary>Microsoft Entra ID sign-in, account file stored in the user's OneDrive app folder.</summary>
        Microsoft
    }

    /// <summary>Parsing helpers for <see cref="StorageProvider"/>.</summary>
    public static class StorageProviderExtensions
    {
        /// <summary>
        /// Parses a provider name (case-insensitive). Defaults to <see cref="StorageProvider.Google"/> for
        /// null/empty/unrecognized values, so sessions created before Microsoft support existed - which have
        /// no provider recorded at all - keep resolving to the only backend they could have used.
        /// </summary>
        public static StorageProvider Parse(string? value)
        {
            if (string.Equals(value, nameof(StorageProvider.Microsoft), StringComparison.OrdinalIgnoreCase))
            {
                return StorageProvider.Microsoft;
            }

            return StorageProvider.Google;
        }
    }
}
