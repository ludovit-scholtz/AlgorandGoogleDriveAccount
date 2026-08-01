namespace BiatecOIDC.Model
{
    /// <summary>Request body for <c>POST /wallet/backup/start</c>.</summary>
    public class StartVaultBackupRequest
    {
        /// <summary>The cloud provider to back the vault up to (e.g. <c>"Microsoft"</c>) - must differ from the caller's primary provider.</summary>
        public string TargetProvider { get; set; } = string.Empty;
    }

    /// <summary>Response body for <c>POST /wallet/backup/start</c>.</summary>
    public class StartVaultBackupResponse
    {
        /// <summary>Opaque id correlating the rest of the flow - pass it to <c>POST /wallet/backup/complete</c> once the browser step below finishes.</summary>
        public string LinkId { get; set; } = string.Empty;

        /// <summary>URL to open in a browser to authorize the target provider. Not an API call - a page for the user to visit.</summary>
        public string AuthorizeUrl { get; set; } = string.Empty;
    }

    /// <summary>Request body for <c>POST /wallet/backup/complete</c>.</summary>
    public class CompleteVaultBackupRequest
    {
        /// <summary>The <see cref="StartVaultBackupResponse.LinkId"/> from <c>POST /wallet/backup/start</c>.</summary>
        public string LinkId { get; set; } = string.Empty;
    }
}
