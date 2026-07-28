namespace BiatecMCP.Model
{
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
}
