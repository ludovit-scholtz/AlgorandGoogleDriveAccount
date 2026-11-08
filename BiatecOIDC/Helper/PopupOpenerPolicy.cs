namespace BiatecOIDC.Helper
{
    /// <summary>
    /// Sends <c>Cross-Origin-Opener-Policy: same-origin</c> on the pages a client shows in a popup (authorization,
    /// provider picker, consent, logout), so those pages cannot reach the opening page through <c>window.opener</c>
    /// (reverse tabnabbing).
    ///
    /// The header severs the opener relation, after which the opener reads <c>popup.closed === true</c> at once.
    /// Clients that treat <c>popup.closed</c> as "the user cancelled" (the Biatec chat widget before its Cancel
    /// button) would fail every sign-in, so the header is OFF unless <c>Oidc:PopupOpenerPolicy:Enabled</c> is true.
    /// </summary>
    public static class PopupOpenerPolicy
    {
        /// <summary>Header name.</summary>
        public const string HeaderName = "Cross-Origin-Opener-Policy";

        /// <summary>Header value.</summary>
        public const string HeaderValue = "same-origin";

        /// <summary>The path prefixes of the pages shown to the user (the token, userinfo and discovery endpoints are not pages).</summary>
        private static readonly string[] PagePrefixes =
        {
            "/authorize",
            "/select-provider",
            "/logout",
            "/connect/endsession",
        };

        /// <summary>True when <paramref name="path"/> is a page a popup shows.</summary>
        public static bool IsPopupPage(PathString path)
        {
            foreach (var prefix in PagePrefixes)
            {
                if (path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>Adds the header to the response of a popup page when <paramref name="enabled"/>.</summary>
        public static void Apply(HttpContext context, bool enabled)
        {
            if (!enabled || !IsPopupPage(context.Request.Path))
            {
                return;
            }
            context.Response.Headers[HeaderName] = HeaderValue;
        }
    }
}
