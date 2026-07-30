namespace BiatecSelfCustodyCore.Model
{
    /// <summary>App-wide host and Drive storage naming, bound from the <c>App</c> configuration section.
    /// Google OAuth credentials live separately in <see cref="GoogleCloudServiceConfiguration"/>
    /// (<c>CloudServices:Google</c>).</summary>
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
    }
}
