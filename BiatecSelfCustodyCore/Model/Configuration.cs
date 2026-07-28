namespace BiatecSelfCustodyCore.Model
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
}
