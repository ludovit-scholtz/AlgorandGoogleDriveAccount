namespace BiatecSelfCustodyCore.Model
{
    /// <summary>Google OAuth app registration credentials, bound from the <c>CloudServices:Google</c> configuration section.</summary>
    public class GoogleCloudServiceConfiguration
    {
        /// <summary>Google OAuth 2.0 client ID.</summary>
        public string? ClientId { get; set; }

        /// <summary>Google OAuth 2.0 client secret.</summary>
        public string? ClientSecret { get; set; }
    }
}
