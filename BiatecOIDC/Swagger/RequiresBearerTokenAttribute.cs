namespace BiatecOIDC.Swagger
{
    /// <summary>
    /// Marks an action that reads its own caller's identity from an <c>Authorization: Bearer</c> header
    /// (via <c>BearerTokenHelper</c> + <c>IJwtIssuerService.ValidateBearerAccessToken</c>) rather than
    /// <c>[Authorize]</c> - these actions are deliberately <c>[AllowAnonymous]</c> so an unauthenticated
    /// caller gets a 401/403 JSON response instead of ASP.NET Core's default challenge redirect (see
    /// <c>.claude/skills/biatec-oidc-jwt/SKILL.md</c>, "Why manual token parsing"). Since there's no
    /// <c>[Authorize]</c> for Swashbuckle to notice, <see cref="BearerAuthOperationFilter"/> looks for this
    /// attribute instead, so Swagger UI still shows the "Authorize" padlock and lets you set the header.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public sealed class RequiresBearerTokenAttribute : Attribute
    {
    }
}
