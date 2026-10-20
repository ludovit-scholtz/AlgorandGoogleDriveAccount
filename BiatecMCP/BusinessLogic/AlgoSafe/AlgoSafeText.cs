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

        private static bool IsInvisible(System.Text.Rune r) =>
            System.Text.Rune.GetUnicodeCategory(r) is System.Globalization.UnicodeCategory.Control
                or System.Globalization.UnicodeCategory.Format
                or System.Globalization.UnicodeCategory.LineSeparator
                or System.Globalization.UnicodeCategory.ParagraphSeparator
                or System.Globalization.UnicodeCategory.PrivateUse
                or System.Globalization.UnicodeCategory.OtherNotAssigned;

        public static string Clean(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            // Control characters, line/paragraph separators and invisible format characters (bidi overrides, zero-width) can fake layout or reorder text.
            // Per code point (not per UTF-16 char): the invisible "tag" characters (U+E0000..) are surrogate pairs.
            var runes = text.EnumerateRunes().Select(r => IsInvisible(r) ? new System.Text.Rune(' ') : r).ToList();
            var cleaned = string.Concat(runes.Take(MaxLength).Select(r => r.ToString())).Trim();
            return runes.Count <= MaxLength ? cleaned : cleaned + "…";
        }
    }
}
