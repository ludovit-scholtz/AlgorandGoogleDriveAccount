using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    /// <summary>
    /// Shared <c>OnRedirectToIdentityProvider</c> logic for both providers in both apps: honors two
    /// <c>AuthenticationProperties.Items</c> conventions set by callers that need more than the scheme's
    /// baseline scopes -
    /// <list type="bullet">
    /// <item><c>incremental_scopes</c> - space-separated extra scope(s) to append to the request.</item>
    /// <item><c>force_consent</c> - any non-empty value forces a fresh consent screen (<c>prompt=consent</c>),
    /// needed when re-requesting a scope the user may have declined the first time - providers don't
    /// reliably re-prompt for a previously-declined scope otherwise.</item>
    /// </list>
    /// </summary>
    public static class OpenIdConnectIncrementalAuth
    {
        public const string IncrementalScopesKey = "incremental_scopes";
        public const string ForceConsentKey = "force_consent";

        public static void Apply(RedirectContext context)
        {
            if (context.Properties.Items.TryGetValue(IncrementalScopesKey, out var extraScopes) && !string.IsNullOrWhiteSpace(extraScopes))
            {
                context.ProtocolMessage.Scope = $"{context.ProtocolMessage.Scope} {extraScopes}".Trim();
            }

            if (context.Properties.Items.ContainsKey(ForceConsentKey))
            {
                context.ProtocolMessage.Prompt = "consent";
            }
        }
    }
}
