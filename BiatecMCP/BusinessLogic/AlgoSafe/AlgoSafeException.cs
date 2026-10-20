namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// A failure with a message that is safe and useful to show to the connected agent (unlike an unexpected exception, whose
    /// text is never leaked - see <c>BiatecMCP.MCP</c>'s <c>SanitizeForToolResponse</c>). <see cref="ErrorType"/> becomes the tool's
    /// <c>ErrorType</c>.
    /// </summary>
    public sealed class AlgoSafeException : Exception
    {
        public AlgoSafeException(string errorType, string message)
            : base(message)
        {
            ErrorType = errorType;
        }

        public string ErrorType { get; }

        /// <summary>Set on a failed simulation whose node message said the opcode budget ran out - the one failure a larger budget can fix.</summary>
        public bool BudgetExhausted { get; init; }
    }
}
