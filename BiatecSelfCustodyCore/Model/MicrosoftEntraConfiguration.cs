namespace BiatecSelfCustodyCore.Model
{
    /// <summary>Microsoft Entra ID app registration settings, bound from the <c>CloudServices:Entra</c> configuration section.</summary>
    public class MicrosoftEntraConfiguration
    {
        /// <summary>Entra tenant ID (or "common"/"organizations"/"consumers" for multi-tenant apps).</summary>
        public string TenantId { get; set; } = "common";

        /// <summary>Application (client) ID of the Entra app registration.</summary>
        public string ClientId { get; set; } = string.Empty;

        /// <summary>Client secret of the Entra app registration.</summary>
        public string ClientSecret { get; set; } = string.Empty;
    }
}
