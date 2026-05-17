using System.Text.RegularExpressions;

namespace AlgorandGoogleDriveAccount.Helper
{
    internal static class RedirectUriMatcher
    {
        private const string WildcardToken = "biatecredirectwildcard";

        public static bool MatchesAuthorizeRedirect(string configuredPattern, string? actualUri)
        {
            if (!Uri.TryCreate(actualUri, UriKind.Absolute, out var requested))
            {
                return false;
            }

            return TryParseConfiguredPattern(configuredPattern, out var pattern)
                   && Matches(pattern, requested, includeQuery: true, normalizeTrailingSlash: false);
        }

        public static bool MatchesPostLogoutRedirect(string configuredPattern, Uri requested)
        {
            return TryParseConfiguredPattern(configuredPattern, out var pattern)
                   && Matches(pattern, requested, includeQuery: false, normalizeTrailingSlash: true);
        }

        private static bool TryParseConfiguredPattern(string configuredPattern, out RedirectUriPattern pattern)
        {
            pattern = null!;

            if (string.IsNullOrWhiteSpace(configuredPattern))
            {
                return false;
            }

            var sanitizedPattern = configuredPattern.Replace("*", WildcardToken, StringComparison.Ordinal);
            if (!Uri.TryCreate(sanitizedPattern, UriKind.Absolute, out var parsedUri))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(parsedUri.Fragment))
            {
                return false;
            }

            pattern = new RedirectUriPattern(
                parsedUri.Scheme,
                RestoreWildcards(parsedUri.Host),
                parsedUri.Port,
                RestoreWildcards(parsedUri.AbsolutePath),
                RestoreWildcards(parsedUri.Query));

            return true;
        }

        private static bool Matches(RedirectUriPattern pattern, Uri requested, bool includeQuery, bool normalizeTrailingSlash)
        {
            if (!string.Equals(pattern.Scheme, requested.Scheme, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (pattern.Port != requested.Port)
            {
                return false;
            }

            if (!WildcardEquals(pattern.HostPattern, requested.Host, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var configuredPath = normalizeTrailingSlash ? NormalizePath(pattern.PathPattern) : pattern.PathPattern;
            var requestedPath = normalizeTrailingSlash ? NormalizePath(requested.AbsolutePath) : requested.AbsolutePath;
            if (!WildcardEquals(configuredPath, requestedPath, StringComparison.Ordinal))
            {
                return false;
            }

            if (!includeQuery)
            {
                return true;
            }

            return WildcardEquals(pattern.QueryPattern, requested.Query, StringComparison.Ordinal);
        }

        private static bool WildcardEquals(string pattern, string value, StringComparison comparison)
        {
            if (!pattern.Contains('*', StringComparison.Ordinal))
            {
                return string.Equals(pattern, value, comparison);
            }

            var regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*", StringComparison.Ordinal) + "$";
            var options = RegexOptions.CultureInvariant;
            if (comparison == StringComparison.OrdinalIgnoreCase)
            {
                options |= RegexOptions.IgnoreCase;
            }

            return Regex.IsMatch(value, regex, options);
        }

        private static string RestoreWildcards(string value)
        {
            return value.Replace(WildcardToken, "*", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizePath(string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(absolutePath))
            {
                return "/";
            }

            if (absolutePath.Length > 1 && absolutePath.EndsWith("/", StringComparison.Ordinal))
            {
                return absolutePath.TrimEnd('/');
            }

            return absolutePath;
        }

        private sealed record RedirectUriPattern(
            string Scheme,
            string HostPattern,
            int Port,
            string PathPattern,
            string QueryPattern);
    }
}