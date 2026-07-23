namespace AlgorandGoogleDriveAccount.Model
{
    /// <summary>Google OAuth client settings and Drive storage naming, bound from the <c>App</c> configuration section.</summary>
    public class Configuration
    {
        /// <summary>Public base URL of this service, used when building absolute redirect/callback URLs.</summary>
        public string Host { get; set; } = "https://google.biatec.io";

        /// <summary>Name of the Google Drive folder that holds the user's encrypted account file.</summary>
        public string StorageFolderName { get; set; } = "Biatec";

        /// <summary>File name of the AES-encrypted Algorand account stored in the user's Drive folder.</summary>
        public string StorageFileName { get; set; } = "AVMAccount.dat";

        /// <summary>Application name reported to the Google Drive API.</summary>
        public string ApplicationName { get; set; } = "Biatec";

        /// <summary>Google OAuth 2.0 client ID.</summary>
        public string ClientId { get; set; }

        /// <summary>Google OAuth 2.0 client secret.</summary>
        public string ClientSecret { get; set; }
    }

    /// <summary>Redis connection settings, bound from the <c>Redis</c> configuration section.</summary>
    public class RedisConfiguration
    {
        /// <summary>StackExchange.Redis connection string used for the distributed cache and session state.</summary>
        public string ConnectionString { get; set; } = "localhost:6379";
    }

    /// <summary>Cross-origin request settings, bound from the <c>Cors</c> configuration section.</summary>
    public class CorsConfiguration
    {
        /// <summary>Origins allowed to call the API with credentials. Empty in Development means any origin is allowed (without credentials); empty in Production means no origin is allowed.</summary>
        public string[] AllowedOrigins { get; set; } = Array.Empty<string>();
    }

    /// <summary>Google Cross-Account Protection (RISC) settings, bound from the <c>CrossAccountProtection</c> configuration section. Disabled by default.</summary>
    public class CrossAccountProtectionConfiguration
    {
        /// <summary>Whether Cross-Account Protection checks are performed at all.</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Whether a security check is required before honoring sensitive operations.</summary>
        public bool RequireSecurityCheck { get; set; } = true;

        /// <summary>How often (in minutes) a cached security check result is considered fresh.</summary>
        public int SecurityCheckIntervalMinutes { get; set; } = 60;

        /// <summary>Whether detected security events are automatically reported to Google.</summary>
        public bool AutoReportEvents { get; set; } = true;

        /// <summary>Whether to request granular (per-scope) consent during the OAuth flow.</summary>
        public bool EnableGranularConsent { get; set; } = false;

        /// <summary>Whether to strip internal/undocumented Google scopes that can trigger consent-screen warnings.</summary>
        public bool FilterInternalScopes { get; set; } = true;
    }

    /// <summary>Algorand node settings per network, bound from the <c>Algod</c> configuration section.</summary>
    public class AlgodConfiguration
    {
        /// <summary>Algod node settings keyed by network name (e.g. "mainnet", "testnet").</summary>
        public Dictionary<string, AlgodNetworkSettings> Networks { get; set; } = new Dictionary<string, AlgodNetworkSettings>();
    }

    /// <summary>Connection details for a single Algorand network.</summary>
    public class AlgodNetworkSettings
    {
        /// <summary>Base URL of the algod REST API for this network.</summary>
        public string ApiAddress { get; set; } = string.Empty;

        /// <summary>API token used to authenticate against the algod node.</summary>
        public string ApiToken { get; set; } = string.Empty;

        /// <summary>Base URL of a block explorer, used to build links to transactions on this network.</summary>
        public string ExplorerBaseUrl { get; set; } = "https://allo.info/tx/";
    }
}
