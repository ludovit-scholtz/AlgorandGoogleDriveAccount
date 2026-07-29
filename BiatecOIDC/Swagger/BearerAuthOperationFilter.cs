using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace BiatecOIDC.Swagger
{
    /// <summary>
    /// Attaches the "Bearer" security requirement to operations marked
    /// <see cref="RequiresBearerTokenAttribute"/>, so Swagger UI's "Authorize" padlock (and the header it
    /// injects into "Try it out" calls) shows up for endpoints that authenticate via a manually-parsed
    /// <c>Authorization: Bearer</c> header instead of <c>[Authorize]</c>.
    /// </summary>
    public class BearerAuthOperationFilter : IOperationFilter
    {
        public void Apply(OpenApiOperation operation, OperationFilterContext context)
        {
            var requiresBearerToken = context.MethodInfo.GetCustomAttributes(true).OfType<RequiresBearerTokenAttribute>().Any();
            if (!requiresBearerToken)
            {
                return;
            }

            operation.Security ??= new List<OpenApiSecurityRequirement>();
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", context.Document)] = new List<string>()
            });
        }
    }
}
