using AlgorandGoogleDriveAccount.Model;

namespace AlgorandGoogleDriveAccount.BusinessLogic
{
    /// <summary>Links a device pairing session to a Google Drive-authorized account, backed by Redis.</summary>
    public interface IDevicePairingService
    {
        /// <summary>Registers a new pairing session so a subsequent OAuth callback can be matched back to it.</summary>
        Task<string> InitiatePairingAsync(string sessionId, string deviceName);

        /// <summary>Stores the Google OAuth tokens obtained for a session after the user completes sign-in.</summary>
        Task<DevicePairingResponse> ProcessPairingCallbackAsync(string sessionId, string email, string accessToken, string? refreshToken);

        /// <summary>Returns the current access token for a paired session, or null if not paired/expired.</summary>
        Task<string?> GetDeviceAccessTokenAsync(string sessionId);

        /// <summary>Returns public-safe pairing info for a session, or null if not paired/expired.</summary>
        Task<PairedDeviceInfo?> GetDeviceInfoAsync(string sessionId);

        /// <summary>Returns full pairing info (including tokens) for a session, for internal/diagnostic use.</summary>
        Task<PairedDeviceInfo?> GetDeviceInfoInternalAsync(string sessionId);

        /// <summary>Removes a paired session from cache.</summary>
        Task<DevicePairingResponse> UnpairDeviceAsync(string sessionId);
    }

    /// <summary>Requests additional (incremental) Google OAuth scopes for the current user.</summary>
    public interface IGoogleAuthorizationService
    {
        /// <summary>Builds an authorization URL requesting the given additional scopes on top of what's already granted.</summary>
        Task<string> GetIncrementalAuthorizationUrlAsync(string[] additionalScopes, string? sessionId = null, string? redirectUri = null);

        /// <summary>Checks whether the current (or given session's) grant already includes a scope.</summary>
        Task<bool> HasScopeAsync(string scope, string? sessionId = null);

        /// <summary>Returns an access token known to carry the given scope, or null if not available.</summary>
        Task<string?> GetAccessTokenWithScopeAsync(string scope, string? sessionId = null);
    }

    /// <summary>Google Cross-Account Protection (RISC) integration: security status checks and event reporting.</summary>
    public interface ICrossAccountProtectionService
    {
        /// <summary>Whether the given user's account currently looks secure (no pending re-auth/warnings).</summary>
        Task<bool> IsUserAccountSecureAsync(string userId);

        /// <summary>Checks the current security status for the account behind the given access token.</summary>
        Task<CrossAccountProtectionStatus> CheckSecurityStatusAsync(string accessToken);

        /// <summary>Reports a security event for a user to Google Cross-Account Protection.</summary>
        Task<bool> ReportSecurityEventAsync(string userId, SecurityEventType eventType, string? details = null);

        /// <summary>Requests that a user re-authenticate for the given scopes.</summary>
        Task<ReauthenticationResult> RequestReauthenticationAsync(string userId, string[] scopes);
    }

    /// <summary>Computes a user's Algorand portfolio value and the resulting service tier.</summary>
    public interface IPortfolioValuationService
    {
        /// <summary>Total portfolio value in EUR.</summary>
        Task<decimal> GetPortfolioValueAsync(string email);

        /// <summary>Service tier (Free/Professional/Enterprise) implied by the current portfolio value.</summary>
        Task<ServiceTier> GetServiceTierAsync(string email);

        /// <summary>Full portfolio summary, including value, tier, and balances.</summary>
        Task<PortfolioSummary> GetPortfolioSummaryAsync(string email);

        /// <summary>Recomputes and caches the portfolio valuation for a user.</summary>
        Task UpdatePortfolioValuationAsync(string email);
    }

    /// <summary>Value-based service tier, driving device limits and support SLA (no billing involved).</summary>
    public enum ServiceTier
    {
        /// <summary>Portfolio value below the Professional threshold.</summary>
        Free,

        /// <summary>Mid-range portfolio value; priority support.</summary>
        Professional,

        /// <summary>High portfolio value; dedicated support.</summary>
        Enterprise
    }

    /// <summary>Snapshot of a user's Algorand portfolio and the service tier it implies.</summary>
    public class PortfolioSummary
    {
        /// <summary>Total portfolio value in EUR.</summary>
        public decimal TotalValueEur { get; set; }

        /// <summary>Service tier implied by <c>TotalValueEur</c>.</summary>
        public ServiceTier CurrentTier { get; set; }

        /// <summary>When this summary was last computed.</summary>
        public DateTime LastUpdated { get; set; }

        /// <summary>Number of Algorand accounts included in the valuation.</summary>
        public int AccountCount { get; set; }

        /// <summary>Native ALGO balance across the user's accounts.</summary>
        public decimal AlgorandBalance { get; set; }

        /// <summary>Value of non-ALGO assets held across the user's accounts.</summary>
        public decimal AssetValue { get; set; }
    }

    /// <summary>Kind of security event reportable to Google Cross-Account Protection.</summary>
    public enum SecurityEventType
    {
        /// <summary>A login attempt flagged as suspicious.</summary>
        SuspiciousLogin,

        /// <summary>Account access from an unusual location, device, or pattern.</summary>
        UnusualAccess,

        /// <summary>A suspected or confirmed data breach affecting the account.</summary>
        DataBreach,

        /// <summary>Evidence the account itself has been compromised.</summary>
        AccountCompromise,

        /// <summary>A phishing attempt targeting the account.</summary>
        PhishingAttempt
    }

    /// <summary>Result of a Cross-Account Protection security status check.</summary>
    public class CrossAccountProtectionStatus
    {
        /// <summary>Whether the account currently looks secure.</summary>
        public bool IsSecure { get; set; }

        /// <summary>Human-readable warnings explaining why the account may not be secure.</summary>
        public string[] SecurityWarnings { get; set; } = Array.Empty<string>();

        /// <summary>When this status was last checked.</summary>
        public DateTime LastSecurityCheck { get; set; }

        /// <summary>Whether the user should be prompted to re-authenticate.</summary>
        public bool RequiresReauthentication { get; set; }
    }

    /// <summary>Result of requesting that a user re-authenticate.</summary>
    public class ReauthenticationResult
    {
        /// <summary>Whether the re-authentication request was issued successfully.</summary>
        public bool Success { get; set; }

        /// <summary>URL the user should be sent to in order to re-authenticate.</summary>
        public string? ReauthUrl { get; set; }

        /// <summary>Error message, if the request could not be issued.</summary>
        public string? ErrorMessage { get; set; }
    }
}
