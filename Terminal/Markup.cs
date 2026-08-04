using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// Turning lines into the markup the page draws.
    ///
    /// Everything here produces ONE element's worth of content: text, <c>&lt;br&gt;</c> between lines, and
    /// <c>&lt;span class&gt;</c> for colour. Nothing else. That is not a stylistic choice - Sideload compiles an
    /// element whose children are all inline into a single text leaf, and a page built that way costs about four
    /// milliseconds to rebuild where one div per line costs thirty-five. At sixty keystrokes a minute the difference
    /// is the difference between a terminal and a slideshow.
    ///
    /// Two consequences the design has to live with, and does: a line cannot be clicked, and a row cannot have a
    /// background. Selection is a colour and a marker, which is what a terminal does anyway.
    /// </summary>
    public static class Markup
    {
        /// <summary>Width of the value column in the suggestion list, in characters. Only meaningful because the
        /// page sets a fixed glyph advance - without that the padding is decorative.</summary>
        private const int ValueColumn = 24;

        /// <summary>Where the source label starts. Wide enough for `long_life_fertilizer` plus the marker.</summary>
        private const int SourceColumn = 30;

        /// <summary>Characters the terminal can fit across the landscape viewport, less a little slack.</summary>
        private const int LineWidth = 88;

        /// <summary>
        /// The block above the prompt: what the command takes, what it does, and the rows.
        ///
        /// Returns an empty string when there is nothing to offer, which hides the block entirely rather than
        /// leaving a gap the transcript cannot use.
        /// </summary>
        public static string Suggestions(SuggestionSet set, int selected, int window)
        {
            if (set == null || !set.Any) return "";

            int count = set.Rows.Count;
            int visible = Math.Min(Hash.Terminal.Suggestions.MaxRows, count);

            int first = Math.Max(0, Math.Min(window, count - visible));
            int last = first + visible;

            var sb = new StringBuilder();

            if (set.Command != null)
            {
                Line(sb, "sig", set.Command.Signature);

                string description = set.Command.Description.Length > 0
                    ? set.Command.Description
                    : "No description.";

                Line(sb, "desc", Clip(description, LineWidth - set.Command.Source.Length - 2)
                                 + "  " + set.Command.Source);
            }

            Line(sb, "rule", Rule(first, last, count));

            for (int i = first; i < last; i++)
            {
                Suggestion row = set.Rows[i];
                bool picked = i == selected;

                var text = new StringBuilder();
                text.Append(picked ? "> " : "  ");
                text.Append(Pad(Clip(row.Value, ValueColumn), SourceColumn - 2));

                if (sb.Length > 0) sb.Append("<br>");

                // The value and its source carry different colours, so the row is two spans rather than one. Still
                // one box: spans are inline, and the whole block compiles to a single text leaf.
                Span(sb, picked ? "pick" : null, text.ToString());
                Span(sb, row.Kind == SuggestionKind.History ? "dim" : row.IsVanilla ? "src-game" : "src-mod",
                     row.Source);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The line between the description and the rows, carrying how far through the list the window is.
        ///
        /// A count rather than an arrow: the game's font atlases are Latin-only, so a triangle or an ellipsis renders
        /// as an empty box. And a count says more - "9-16/63" tells you both that there is more and how much, which
        /// an arrow never does.
        /// </summary>
        private static string Rule(int first, int last, int count)
        {
            if (count <= Hash.Terminal.Suggestions.MaxRows) return new string('-', LineWidth);

            string where = $" {first + 1}-{last} of {count} ";
            int dashes = Math.Max(4, LineWidth - where.Length);

            return new string('-', dashes) + where;
        }

        /// <summary>The transcript window as one block.</summary>
        public static string Transcript(IReadOnlyList<OutputLine> lines)
        {
            if (lines == null || lines.Count == 0) return "";

            var sb = new StringBuilder();

            foreach (OutputLine line in lines)
            {
                if (sb.Length > 0) sb.Append("<br>");
                Span(sb, Class(line.Kind), line.Text);
            }

            return sb.ToString();
        }

        private static string Class(LineKind kind) => kind switch
        {
            LineKind.Echo => "echo",
            LineKind.Warn => "warn",
            LineKind.Error => "err",
            LineKind.Dim => "dim",
            _ => null,
        };

        private static void Line(StringBuilder sb, string cls, string text)
        {
            if (sb.Length > 0) sb.Append("<br>");
            Span(sb, cls, text);
        }

        private static void Span(StringBuilder sb, string cls, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            if (cls == null) { sb.Append(Escape(text)); return; }

            sb.Append("<span class=\"").Append(cls).Append("\">").Append(Escape(text)).Append("</span>");
        }

        /// <summary>
        /// Escape text that is about to become markup.
        ///
        /// Not optional and not paranoia: half the signatures the terminal prints contain angle brackets, so
        /// `give &lt;item&gt;` would lose its argument to a tag that does not exist. Item ids come from other mods
        /// and are not trustworthy input either.
        /// </summary>
        public static string Escape(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (text.IndexOf('&') < 0 && text.IndexOf('<') < 0 && text.IndexOf('>') < 0) return text;

            return text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        }

        /// <summary>Pad to a column. Never truncates - a value one character too long pushes its own row out rather
        /// than losing a letter, which is easier to read than a mystery.</summary>
        internal static string Pad(string value, int width)
        {
            value ??= "";
            return value.Length >= width ? value + " " : value.PadRight(width);
        }

        /// <summary>Shorten to fit, ending in "..". Three dots would be tempting and U+2026 would be worse - the
        /// game's font atlases are Latin-only and an ellipsis renders as an empty box.</summary>
        internal static string Clip(string value, int width)
        {
            if (value == null) return "";
            if (width < 4 || value.Length <= width) return value;

            return value.Substring(0, width - 2) + "..";
        }
    }
}
