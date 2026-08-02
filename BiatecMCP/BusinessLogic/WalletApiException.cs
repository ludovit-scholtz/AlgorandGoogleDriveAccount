namespace BiatecMCP.BusinessLogic
{
    /// <summary>
    /// Thrown by <see cref="IBiatecWalletClient"/> when BiatecOIDC's wallet API rejects a call - carries
    /// the same <c>ProblemDetails</c> title/detail BiatecOIDC returns (e.g. <c>spending_limit_exceeded</c>,
    /// <c>insufficient_scope</c>, <c>storage_access_denied</c>) so callers (the MCP tools) can surface a
    /// clear, specific reason to the connected AI client instead of a generic failure.
    /// </summary>
    public sealed class WalletApiException : Exception
    {
        /// <summary>The HTTP status code BiatecOIDC responded with.</summary>
        public int StatusCode { get; }

        /// <summary>The <c>ProblemDetails.Title</c> (a stable, machine-readable error code, e.g. <c>spending_limit_exceeded</c>).</summary>
        public string ErrorCode { get; }

        public WalletApiException(int statusCode, string errorCode, string? detail)
            : base(string.IsNullOrWhiteSpace(detail) ? errorCode : detail)
        {
            StatusCode = statusCode;
            ErrorCode = errorCode;
        }
    }
}
