using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace BiatecOIDC.Helper
{
    /// <summary>Raised when a JSON value cannot be put in RFC 8785 canonical form.</summary>
    public sealed class JsonCanonicalizationException : Exception
    {
        public JsonCanonicalizationException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// RFC 8785 (JSON Canonicalization Scheme). Receipts are signed over these exact bytes, so the output must match every
    /// other implementation byte for byte (see the vectors of the Biatec consent protocol).
    ///
    /// Strict on purpose, because the result is what a signature covers: duplicate object member names, lone surrogates,
    /// non-finite numbers and integers beyond 2^53-1 are refused instead of being normalised.
    /// </summary>
    public static class JsonCanonicalizer
    {
        private const long MaxSafeInteger = 9007199254740991;

        /// <summary>Canonical text of <paramref name="json"/>.</summary>
        public static string Canonicalize(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                return Canonicalize(doc.RootElement);
            }
            catch (JsonException ex)
            {
                throw new JsonCanonicalizationException("Not valid JSON: " + ex.Message);
            }
        }

        /// <summary>Canonical text of <paramref name="element"/>.</summary>
        public static string Canonicalize(JsonElement element)
        {
            var sb = new StringBuilder();
            try
            {
                Write(element, sb);
            }
            catch (InvalidOperationException ex)
            {
                // System.Text.Json refuses to read a string with a lone surrogate escape.
                throw new JsonCanonicalizationException("Unreadable JSON value: " + ex.Message);
            }
            return sb.ToString();
        }

        /// <summary>The UTF-8 bytes that get signed.</summary>
        public static byte[] CanonicalizeToUtf8(JsonElement element) => Encoding.UTF8.GetBytes(Canonicalize(element));

        private static void Write(JsonElement e, StringBuilder sb)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Null:
                    sb.Append("null");
                    break;
                case JsonValueKind.True:
                    sb.Append("true");
                    break;
                case JsonValueKind.False:
                    sb.Append("false");
                    break;
                case JsonValueKind.String:
                    WriteString(e.GetString()!, sb);
                    break;
                case JsonValueKind.Number:
                    sb.Append(FormatNumber(e));
                    break;
                case JsonValueKind.Array:
                    sb.Append('[');
                    var first = true;
                    foreach (var item in e.EnumerateArray())
                    {
                        if (!first)
                        {
                            sb.Append(',');
                        }
                        first = false;
                        Write(item, sb);
                    }
                    sb.Append(']');
                    break;
                case JsonValueKind.Object:
                    var members = new List<(string Name, JsonElement Value)>();
                    foreach (var p in e.EnumerateObject())
                    {
                        members.Add((p.Name, p.Value));
                    }
                    // string.CompareOrdinal compares UTF-16 code units, which is the order RFC 8785 requires.
                    members.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                    for (var i = 1; i < members.Count; i++)
                    {
                        if (members[i].Name == members[i - 1].Name)
                        {
                            throw new JsonCanonicalizationException("Duplicate object member name.");
                        }
                    }
                    sb.Append('{');
                    for (var i = 0; i < members.Count; i++)
                    {
                        if (i > 0)
                        {
                            sb.Append(',');
                        }
                        WriteString(members[i].Name, sb);
                        sb.Append(':');
                        Write(members[i].Value, sb);
                    }
                    sb.Append('}');
                    break;
                default:
                    throw new JsonCanonicalizationException("Unsupported JSON value.");
            }
        }

        private static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            for (var i = 0; i < s.Length; i++)
            {
                var c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20)
                        {
                            sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else if (char.IsHighSurrogate(c))
                        {
                            if (i + 1 >= s.Length || !char.IsLowSurrogate(s[i + 1]))
                            {
                                throw new JsonCanonicalizationException("Lone surrogate in a string.");
                            }
                            sb.Append(c).Append(s[++i]);
                        }
                        else if (char.IsLowSurrogate(c))
                        {
                            throw new JsonCanonicalizationException("Lone surrogate in a string.");
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            sb.Append('"');
        }

        private static string FormatNumber(JsonElement e)
        {
            var raw = e.GetRawText();
            if (raw.IndexOfAny(new[] { '.', 'e', 'E' }) < 0
                && BigInteger.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var big)
                && BigInteger.Abs(big) > MaxSafeInteger)
            {
                throw new JsonCanonicalizationException("Integer beyond 2^53-1; carry large amounts as strings.");
            }

            var d = e.GetDouble();
            if (double.IsNaN(d) || double.IsInfinity(d))
            {
                throw new JsonCanonicalizationException("Non-finite number.");
            }
            return FormatDouble(d);
        }

        /// <summary>ECMAScript Number-to-string (ECMA-262 7.1.12.1), which RFC 8785 uses for numbers.</summary>
        internal static string FormatDouble(double value)
        {
            if (value == 0)
            {
                return "0"; // also -0
            }

            var negative = value < 0;
            // "R" is the shortest string that round-trips, as the algorithm needs; only its digits and exponent are used.
            var r = Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);
            var exp = 0;
            var ePos = r.IndexOfAny(new[] { 'E', 'e' });
            if (ePos >= 0)
            {
                exp = int.Parse(r[(ePos + 1)..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                r = r[..ePos];
            }

            var dot = r.IndexOf('.');
            var intLen = dot < 0 ? r.Length : dot;
            var digits = r.Replace(".", "");
            var n = intLen + exp; // value = 0.digits x 10^n
            var lead = 0;
            while (lead < digits.Length - 1 && digits[lead] == '0')
            {
                lead++;
            }
            digits = digits[lead..];
            n -= lead;
            digits = digits.TrimEnd('0');
            var k = digits.Length;

            string body;
            if (k <= n && n <= 21)
            {
                body = digits + new string('0', n - k);
            }
            else if (0 < n && n <= 21)
            {
                body = digits[..n] + "." + digits[n..];
            }
            else if (-6 < n && n <= 0)
            {
                body = "0." + new string('0', -n) + digits;
            }
            else
            {
                var e10 = n - 1;
                var sign = e10 < 0 ? "-" : "+";
                var mant = k == 1 ? digits : digits[..1] + "." + digits[1..];
                body = mant + "e" + sign + Math.Abs(e10).ToString(CultureInfo.InvariantCulture);
            }
            return negative ? "-" + body : body;
        }
    }
}
