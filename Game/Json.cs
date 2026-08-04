using System.Globalization;
using System.Text;

namespace Hash
{
    /// <summary>
    /// Just enough JSON to talk to the page.
    ///
    /// A serializer would be a dependency shipped in every install for four field types, and the message shapes here
    /// are fixed by the two ends agreeing rather than by anything discovered at runtime. Writing is a StringBuilder;
    /// reading is one field at a time, because the page never sends anything nested.
    /// </summary>
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new("{");

        internal Json Str(string name, string value) => Put(name, Quote(value));

        internal Json Num(string name, int value) => Put(name, value.ToString(CultureInfo.InvariantCulture));

        internal Json Bool(string name, bool value) => Put(name, value ? "true" : "false");

        /// <summary>An already-serialised value - an array built elsewhere.</summary>
        internal Json Raw(string name, string json) => Put(name, json ?? "null");

        internal string Done() => _sb.Append('}').ToString();

        private Json Put(string name, string value)
        {
            if (_sb.Length > 1) _sb.Append(',');

            _sb.Append(Quote(name)).Append(':').Append(value);
            return this;
        }

        /// <summary>
        /// A JSON string.
        ///
        /// The control-character escape is not decoration: a captured log line can contain anything a mod put in it,
        /// and one raw newline in the middle of a value turns the whole message into a parse error on the page - a
        /// terminal that goes blank because someone logged a tab.
        /// </summary>
        internal static string Quote(string value)
        {
            if (value == null) return "\"\"";

            var sb = new StringBuilder(value.Length + 2).Append('"');

            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }

            return sb.Append('"').ToString();
        }

        /// <summary>
        /// Read one string field out of a flat object.
        ///
        /// Deliberately not a parser: the page sends exactly two shapes, both flat, both written by code in this
        /// repo. A scanner that understands escapes and nothing else is the honest size for that, and it cannot fail
        /// in a way that needs debugging at two in the morning.
        /// </summary>
        internal static string Field(string json, string name)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(name)) return "";

            string key = "\"" + name + "\"";
            int at = json.IndexOf(key, StringComparison.Ordinal);
            if (at < 0) return "";

            int colon = json.IndexOf(':', at + key.Length);
            if (colon < 0) return "";

            int i = colon + 1;
            while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
            if (i >= json.Length || json[i] != '"') return "";

            var sb = new StringBuilder();

            for (i++; i < json.Length; i++)
            {
                char c = json[i];

                if (c == '\\' && i + 1 < json.Length)
                {
                    char next = json[++i];
                    switch (next)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'u' when i + 4 < json.Length:
                            if (int.TryParse(json.Substring(i + 1, 4), NumberStyles.HexNumber,
                                             CultureInfo.InvariantCulture, out int code))
                            {
                                sb.Append((char)code);
                                i += 4;
                            }
                            break;
                        default: sb.Append(next); break;
                    }
                    continue;
                }

                if (c == '"') break;

                sb.Append(c);
            }

            return sb.ToString();
        }
    }
}
