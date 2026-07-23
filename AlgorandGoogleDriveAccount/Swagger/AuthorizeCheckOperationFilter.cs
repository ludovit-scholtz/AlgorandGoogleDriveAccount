using Microsoft.AspNetCore.Authorization;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace AlgorandGoogleDriveAccount.Swagger
{
    /// <summary>
    /// Attaches the "GoogleCookieAuth" security requirement to operations that actually require
    /// [Authorize], mirroring ASP.NET Core's own attribute evaluation (including [AllowAnonymous]
    /// overrides), so Swagger UI only shows a padlock on endpoints that need a login first.
    /// </summary>
    public class AuthorizeCheckOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var hasAuthorize =
                context.MethodInfo.DeclaringType?.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any() == true ||
                context.MethodInfo.GetCustomAttributes(true).OfType<AuthorizeAttribute>().Any();

            var hasAllowAnonymous = context.MethodInfo.GetCustomAttributes(true).OfType<AllowAnonymousAttribute>().Any();

            if (!hasAuthorize || hasAllowAnonymous)
            {
                return;
            }

            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("GoogleCookieAuth", context.Document)] = new List<string>()
            });
        }
    }
}
