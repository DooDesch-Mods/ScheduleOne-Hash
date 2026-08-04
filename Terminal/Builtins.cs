namespace Hash.Terminal
{
    /// <summary>
    /// The commands the terminal answers itself.
    ///
    /// Every one of them is something the vanilla console cannot do, and most of them are things it should have had:
    /// there is no `help`, no way to see what a command printed a minute ago, and no way to shorten anything. They
    /// are real command words rather than a prefixed syntax because none of them collides - the game registers no
    /// `help`, no `clear`, no `alias`.
    ///
    /// A builtin never shadows a game command. The check runs against the live catalogue, so a mod that one day adds
    /// its own `grep` wins and this one steps aside.
    /// </summary>
    public sealed class Builtins
    {
        private readonly Suggestions _suggestions;
        private readonly ICommandCatalogue _catalogue;
        private readonly History _history;
        private readonly Aliases _aliases;
        private readonly Transcript _transcript;

        public Builtins(Suggestions suggestions, ICommandCatalogue catalogue,
                        History history, Aliases aliases, Transcript transcript)
        {
            _suggestions = suggestions;
            _catalogue = catalogue;
            _history = history;
            _aliases = aliases;
            _transcript = transcript;
        }

        /// <summary>Words this terminal answers, for `help` and for the completion list.</summary>
        public static readonly string[] Words =
        {
            "help", "clear", "history", "alias", "unalias", "grep", "copy", "logs", "raw", "repeat",
        };

        /// <summary>Set by `copy`, read by the host, which owns the clipboard. Cleared once taken.</summary>
        public string PendingClipboard { get; private set; }

        public string TakeClipboard()
        {
            string value = PendingClipboard;
            PendingClipboard = null;
            return value;
        }

        /// <summary>Whether the log view is showing, and what it is filtered to.</summary>
        public bool LogsOpen { get; private set; }

        public string LogsFilter { get; private set; } = "";

        /// <summary>
        /// Try to answer the line here. Returns false when it is not a builtin, or when a game command of the same
        /// name exists and should win.
        /// </summary>
        public bool TryRun(string line, out IReadOnlyList<OutputLine> output)
        {
            output = null;

            List<string> tokens = CommandLine.Tokenise(line);
            if (tokens.Count == 0) return false;

            string word = tokens[0].ToLowerInvariant();

            if (Array.IndexOf(Words, word) < 0) return false;
            if (_suggestions.IsCommand(word)) return false;   // the game got there first

            string rest = line.Length > tokens[0].Length ? line.Substring(tokens[0].Length).Trim() : "";
            var lines = new List<OutputLine>();

            switch (word)
            {
                case "help": Help(rest, lines); break;
                case "clear": _transcript.Clear(); break;
                case "history": HistoryList(rest, lines); break;
                case "alias": Alias(rest, tokens, lines); break;
                case "unalias": Unalias(rest, lines); break;
                case "grep": Grep(rest, lines); break;
                case "copy": Copy(rest, lines); break;
                case "logs": Logs(rest, lines); break;

                // Both are handled by the parser before anything gets here. Reaching this point means the parser let
                // a bare word through, so say what it needs rather than "command not found".
                case "raw": lines.Add(OutputLine.Error("raw needs a command after it.")); break;
                case "repeat": lines.Add(OutputLine.Error("repeat needs a count and a command.")); break;
            }

            output = lines;
            return true;
        }

        // --------------------------------------------------------------------------------------------- help --

        private void Help(string query, List<OutputLine> lines)
        {
            if (query.Length == 0)
            {
                // A grid of words, not a line each.
                //
                // Sixty-three commands with descriptions is sixty-three lines, and the terminal draws eighteen - so
                // the one command you were looking for scrolled away before you could read it, and `help` was
                // useless precisely because it printed everything. Five to a line fits on screen, and `help <word>`
                // is one keystroke away for the one you want.
                lines.Add(OutputLine.Dim($"{_catalogue.Commands.Count} commands, {Words.Length} built in. "
                                         + "help <word> explains one, help <text> searches."));

                foreach (string row in Grid(_catalogue.Commands, Words)) lines.Add(OutputLine.Out(row));
                return;
            }

            CommandInfo exact = _suggestions.Find(query);
            if (exact != null) { Detail(exact, lines); return; }

            var hits = new List<CommandInfo>();
            foreach (CommandInfo command in _catalogue.Commands)
            {
                if (FuzzyMatcher.IsMatch(command.Word, query)
                    || command.Description.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                    hits.Add(command);
            }

            if (hits.Count == 0)
            {
                lines.Add(OutputLine.Warn($"Nothing matches '{query}'."));
                return;
            }

            lines.Add(OutputLine.Dim($"{hits.Count} match '{query}':"));
            foreach (CommandInfo command in hits) lines.Add(OutputLine.Out(Row(command)));
        }

        private static void Detail(CommandInfo command, List<OutputLine> lines)
        {
            lines.Add(OutputLine.Out(command.Signature));

            if (command.Description.Length > 0) lines.Add(OutputLine.Out("  " + command.Description));
            if (command.Usage.Length > 0) lines.Add(OutputLine.Dim("  e.g. " + command.Usage));

            lines.Add(OutputLine.Dim("  " + command.Source));
        }

        private static string Row(CommandInfo command)
        {
            string description = command.Description.Length > 0 ? command.Description : command.Signature;
            return Pad(command.Word, 22) + description;
        }

        /// <summary>Command words in columns, alphabetically, the game's own first and this terminal's after.</summary>
        private static IEnumerable<string> Grid(IReadOnlyList<CommandInfo> commands, IReadOnlyList<string> builtins)
        {
            const int Columns = 5;
            const int Width = 19;

            var words = new List<string>();
            foreach (CommandInfo command in commands) words.Add(command.Word);

            var mine = new List<string>(builtins);
            mine.Sort(StringComparer.OrdinalIgnoreCase);
            words.AddRange(mine);

            for (int i = 0; i < words.Count; i += Columns)
            {
                var row = new System.Text.StringBuilder();

                for (int c = 0; c < Columns && i + c < words.Count; c++)
                    row.Append(Pad(words[i + c], Width));

                yield return row.ToString().TrimEnd();
            }
        }

        // ------------------------------------------------------------------------------------------ history --

        private void HistoryList(string query, List<OutputLine> lines)
        {
            List<string> hits = _history.Search(query);

            if (hits.Count == 0)
            {
                lines.Add(OutputLine.Dim(query.Length == 0 ? "No history yet." : $"No history matches '{query}'."));
                return;
            }

            // Oldest first, so reading top to bottom is reading forwards in time.
            for (int i = hits.Count - 1; i >= 0; i--)
                lines.Add(OutputLine.Out(Pad((hits.Count - i).ToString(), 6) + hits[i]));
        }

        // -------------------------------------------------------------------------------------------- alias --

        private void Alias(string rest, List<string> tokens, List<OutputLine> lines)
        {
            if (rest.Length == 0)
            {
                if (_aliases.Count == 0) { lines.Add(OutputLine.Dim("No aliases. alias <name> <command> makes one.")); return; }

                foreach (KeyValuePair<string, string> pair in _aliases.All)
                    lines.Add(OutputLine.Out(Pad(pair.Key, 16) + pair.Value));

                return;
            }

            if (tokens.Count < 3)
            {
                lines.Add(OutputLine.Error("alias <name> <command>. Quote the command if it has spaces."));
                return;
            }

            string name = tokens[1];
            string expansion = rest.Substring(name.Length).Trim();

            if (!_aliases.TrySet(name, expansion, _suggestions.IsCommand, out string error))
            {
                lines.Add(OutputLine.Error(error));
                return;
            }

            lines.Add(OutputLine.Out($"{name} -> {expansion}"));
        }

        private void Unalias(string rest, List<OutputLine> lines)
        {
            if (rest.Length == 0) { lines.Add(OutputLine.Error("unalias <name>.")); return; }

            lines.Add(_aliases.Remove(rest)
                ? OutputLine.Out($"{rest} removed.")
                : OutputLine.Warn($"No alias called '{rest}'."));
        }

        // ------------------------------------------------------------------------------------- grep and copy --

        private void Grep(string pattern, List<OutputLine> lines)
        {
            if (pattern.Length == 0) { lines.Add(OutputLine.Error("grep <text> filters what is on screen.")); return; }

            var hits = new List<OutputLine>();
            foreach (OutputLine line in _transcript.Lines)
                if (line.Text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(line);

            if (hits.Count == 0) { lines.Add(OutputLine.Dim($"Nothing on screen matches '{pattern}'.")); return; }

            lines.Add(OutputLine.Dim($"{hits.Count} line(s) match '{pattern}':"));
            lines.AddRange(hits);
        }

        /// <summary>
        /// Put something on the system clipboard - the one thing that has to leave the game, and the reason it
        /// exists: an item id read off the screen is otherwise retyped by hand into Discord.
        ///
        /// With no argument it takes the last line that was not framing, which is almost always what was meant.
        /// </summary>
        private void Copy(string rest, List<OutputLine> lines)
        {
            string value = rest;

            if (value.Length == 0)
            {
                foreach (OutputLine line in _transcript.Recent(40))
                {
                    if (line.Kind == LineKind.Dim || line.Kind == LineKind.Echo) continue;
                    value = line.Text.Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(value)) { lines.Add(OutputLine.Warn("Nothing to copy.")); return; }

            PendingClipboard = value;
            lines.Add(OutputLine.Dim($"Copied: {value}"));
        }

        // --------------------------------------------------------------------------------------------- logs --

        private void Logs(string rest, List<OutputLine> lines)
        {
            if (string.Equals(rest, "off", StringComparison.OrdinalIgnoreCase))
            {
                LogsOpen = false;
                LogsFilter = "";
                lines.Add(OutputLine.Dim("Log view off."));
                return;
            }

            LogsOpen = true;
            LogsFilter = rest;

            lines.Add(OutputLine.Dim(rest.Length == 0
                ? "Log view on - everything the game logs. 'logs warn' filters, 'logs off' stops."
                : $"Log view on, filtered to '{rest}'. 'logs off' stops."));
        }

        internal static string Pad(string value, int width)
        {
            value ??= "";
            return value.Length >= width ? value + " " : value.PadRight(width);
        }
    }
}
