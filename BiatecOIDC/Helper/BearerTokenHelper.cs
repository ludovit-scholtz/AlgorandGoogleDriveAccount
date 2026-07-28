namespace BiatecOIDC.Helper
{
    /// <summary>Extracts a <c>Bearer</c> token from an incoming request's <c>Authorization</c> header.</summary>
    public static class BearerTokenHelper
    {
        private const string Prefix = "Bearer ";

        public static string? ExtractBearerToken(HttpRequest request)
        {
            var header = request.Headers.Authorization.ToString();
            if (string.IsNullOrWhiteSpace(header) || !header.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return header[Prefix.Length..].Trim();
        }
    }
}
