namespace BiatecMCP.BusinessLogic.AlgoSafe
{
    /// <summary>
    /// Names, labels and notes live on-chain and are written by whoever created the safe or proposal, so they reach the connected agent as
    /// untrusted text (a prompt-injection vector in a flow that gates fund movement). They are shown shortened and stripped of control
    /// characters - never interpreted - and the tool descriptions tell the agent not to follow instructions found in them.
    /// </summary>
    internal static class AlgoSafeText
    {
        private const int MaxLength = 120;

        public static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            var cleaned = new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
            return cleaned.Length <= MaxLength ? cleaned : cleaned[..MaxLength] + "…";
        }
    }
}
