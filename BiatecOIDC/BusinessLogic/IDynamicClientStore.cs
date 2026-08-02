using BiatecOIDC.Model;

namespace BiatecOIDC.BusinessLogic
{
    /// <summary>
    /// Redis-backed storage for clients registered at runtime via <c>POST /register</c> (RFC 7591 Dynamic
    /// Client Registration) - as opposed to <c>JwtIssuer:Clients</c>, which is static, operator-edited
    /// configuration. See <c>IJwtIssuerService.ResolveClientAsync</c> for how the two are merged (static
    /// config always wins on a matching <c>ClientId</c>, so an operator can hand-upgrade a dynamically
    /// registered client later without deleting it from here first).
    /// </summary>
    public interface IDynamicClientStore
    {
        /// <summary>Persists a newly-registered public client. Never expires - see <see cref="DynamicClientStore"/>'s remarks.</summary>
        Task SaveAsync(JwtIssuerClientConfiguration client);

        /// <summary>Looks up a dynamically-registered client by id, or <c>null</c> if none exists under that id.</summary>
        Task<JwtIssuerClientConfiguration?> GetAsync(string clientId);
    }
}
