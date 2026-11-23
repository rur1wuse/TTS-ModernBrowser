using System;
using System.Text;

namespace TtsModernBrowser
{
    /// <summary>Minimal JS string literal encoder for ExecuteScript payloads.</summary>
    internal static class JsLiteral
    {
        internal static string Quote(string value)
        {
            if (value == null) return "null";

            var sb = new StringBuilder(value.Length + 8);
            sb.Append('"');
            foreach (var c in value)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    default:
                        if (c < 0x20 || c == 0x2028 || c == 0x2029)
                            sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else
                            sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
