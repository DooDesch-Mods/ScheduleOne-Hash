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
        public static string Suggestions(SuggestionSet set, int selected, int window, bool expanded)
        {
            if (set == null) return "";

            // A known command keeps its header even with nothing to offer. Most second arguments are a number or a
            // free string that no provider can list - `give <item> [quantity]` is the obvious one - so hiding the
            // block for want of rows took the shape away at the exact moment it was the only thing left to say, and
            // the player had no way to learn the command took a quantity at all.
            if (set.Command == null && !set.Any) return "";

            var sb = new StringBuilder();

            if (set.Command != null)
            {
                Signature(sb, set.Command.Signature, set.ArgIndex);

                string description = set.Command.Description.Length > 0
                    ? set.Command.Description
                    : "No description.";

                Line(sb, "desc", Clip(description, LineWidth - set.Command.Source.Length - 2)
                                 + "  " + set.Command.Source);
            }

            // Closed: say what Tab would insert and stop there.
            //
            // The block sits between the transcript and the prompt and pushes the transcript up, so eight rows of
            // suggestions cost eight lines of whatever the last command printed - which is most of the screen, and
            // exactly the lines you were trying to read. Shut, it costs one line and still answers the question the
            // list was open for: what happens if I press Tab.
            if (!expanded)
            {
                if (set.Any)
                {
                    // Key first, then the verb, two spaces between pairs - the shape every footer key strip uses
                    // ("F1 Help  F5 Refresh  q Quit"). A sentence in its place reads as prose the eye has to
                    // parse; this reads as a row of keys, which is what it is.
                    Suggestion pick = set.Rows[Math.Min(Math.Max(selected, 0), set.Rows.Count - 1)];

                    Line(sb, "ghost", Pad("tab  " + pick.Value, ValueColumn + 8)
                                      + (set.Rows.Count > 1 ? $"up/down  browse {set.Rows.Count}" : ""));
                }

                return sb.ToString();
            }

            if (!set.Any) return sb.ToString();

            int count = set.Rows.Count;
            int visible = Math.Min(Hash.Terminal.Suggestions.MaxRows, count);

            int first = Math.Max(0, Math.Min(window, count - visible));
            int last = first + visible;

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
                Span(sb, row.Kind == SuggestionKind.History ? "src-history"
                                                            : row.IsVanilla ? "src-game" : "src-mod",
                     row.Source);
            }

            return sb.ToString();
        }

        /// <summary>
        /// The command's shape, with the argument the caret sits in picked out.
        ///
        /// Highlighting is the half that makes the shape useful past the first argument. `setquestentrystate
        /// &lt;quest&gt; &lt;entry&gt; &lt;state&gt;` printed flat tells you three things are wanted but not which one
        /// you are typing, and counting spaces backwards to work it out is exactly the work the line is meant to save.
        ///
        /// Three spans at most, never nested: whichever run comes before the current argument, the argument, and
        /// whatever follows.
        /// </summary>
        private static void Signature(StringBuilder sb, string signature, int argIndex)
        {
            if (string.IsNullOrEmpty(signature)) return;

            var parts = new List<string>(signature.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

            // Part 0 is the command word. A caret past the last argument highlights nothing rather than clamping to
            // the end - the game ignores the extra token, and pointing at one that is not there would say it does not.
            int wanted = argIndex + 1;
            if (wanted <= 0 || wanted >= parts.Count) { Span(sb, "sig", signature); return; }

            Span(sb, "sig", string.Join(" ", parts.GetRange(0, wanted)) + " ");
            Span(sb, "cur", parts[wanted]);

            if (wanted + 1 < parts.Count)
                Span(sb, "sig", " " + string.Join(" ", parts.GetRange(wanted + 1, parts.Count - wanted - 1)));
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
