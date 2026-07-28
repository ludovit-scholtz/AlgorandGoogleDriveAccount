using BiatecSelfCustodyCore.Model;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;

namespace BiatecSelfCustodyCore.BusinessLogic
{
    public class MicrosoftAuthProvider : IMicrosoftAuthProvider
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public MicrosoftAuthProvider(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }

        public async Task<string?> GetAccessTokenAsync()
        {
            var httpContext = _httpContextAccessor.HttpContext;
            if (httpContext == null)
            {
                return null;
            }

            return await httpContext.GetTokenAsync(AuthSchemeNames.Microsoft, "access_token");
        }
    }
}
