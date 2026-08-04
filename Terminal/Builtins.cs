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

        /// <summary>The label in the right-hand column of a row for one of these.</summary>
        public const string Source = "hash";

        /// <summary>
        /// What this terminal answers itself, described the way a registered command would be.
        ///
        /// Written out rather than kept as a list of words because everything downstream treats a command as one of
        /// these: the completion list offers it, the line above the prompt shows its shape, and `help grep` needs
        /// something to print. As bare words they existed only for whoever already knew they were there.
        /// </summary>
        public static readonly IReadOnlyList<CommandInfo> Catalogue = new[]
        {
            Own("help", "help [word|text]", "explain one command, or search for one", "help give"),
            Own("clear", "clear", "empty the screen", "clear"),
            Own("history", "history [text]", "what you have run before, oldest first", "history give"),
            Own("alias", "alias [name] [command]", "make a short name for a command, or list them",
                "alias gk \"give ogkush 5\""),
            Own("unalias", "unalias <name>", "remove an alias", "unalias gk"),
            Own("grep", "grep <text>", "show only the lines on screen containing something", "grep ogkush"),
            Own("copy", "copy [text]", "put a line on the system clipboard - the last one, unless you name it",
                "copy ogkushseed"),
            Own("logs", "logs [filter|off]", "show what the game logs as it happens", "logs warn"),
            Own("raw", "raw <command>", "run the rest of the line as one command, semicolons and all",
                "raw bind t 'settime 1200'"),
            Own("repeat", "repeat <count> <command>", "run a command several times", "repeat 5 give ogkush 1"),
        };

        /// <summary>Words this terminal answers, for the `help` grid and the shadowing check.</summary>
        public static readonly string[] Words = Vocabulary();

        private static CommandInfo Own(string word, string signature, string description, string usage) =>
            new CommandInfo(word, description, usage, signature, Source, isVanilla: false);

        private static string[] Vocabulary()
        {
            var words = new string[Catalogue.Count];
            for (int i = 0; i < Catalogue.Count; i++) words[i] = Catalogue[i].Word;

            return words;
        }

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
                case "raw": lines.Add(OutputLine.Error("raw: no command after it")); UsageLine("raw", lines); break;
                case "repeat": lines.Add(OutputLine.Error("repeat: no count and no command")); UsageLine("repeat", lines); break;
            }

            output = lines;
            return true;
        }

        // --------------------------------------------------------------------------------------------- help --

        private void Help(string query, List<OutputLine> lines)
        {
            if (query.Length == 0) { Brief(lines); return; }

            if (query.StartsWith("all", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(query.Substring(3).Trim(), out int page);
                Everything(Math.Max(1, page), lines);
                return;
            }

            // A topic is a help page of its own: `help world` is the natural next thing to type once `help` has
            // shown that "world" exists and holds eight commands.
            if (HelpTopics.IsTopic(query)) { OneTopic(query, lines); return; }

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
                lines.Add(OutputLine.Warn($"help: nothing matches '{query}'"));
                return;
            }

            lines.Add(OutputLine.Dim($"{hits.Count} match '{query}':"));
            foreach (CommandInfo command in hits) lines.Add(OutputLine.Out(Row(command)));
        }

        /// <summary>
        /// Plain `help`: a few commands worth knowing, then a map of everything else.
        ///
        /// Laid out the way every large CLI lays this out - one command per line, its description in a second
        /// column - because that is the only shape a list of commands can be READ in. Words packed several to a
        /// line fit more on screen and say nothing: they are indistinguishable from each other, which is what a
        /// list of names without meanings always is.
        ///
        /// The map is the other half, and the answer to "what else is there". Every entry on it opens with
        /// `help &lt;topic&gt;`, so nothing is hidden - it is one word away instead of fifteen lines away.
        /// </summary>
        private void Brief(List<OutputLine> lines)
        {
            foreach (string word in HelpTopics.Common)
            {
                CommandInfo command = _suggestions.Find(word);
                if (command != null) lines.Add(OutputLine.Out(Row(command)));
            }

            lines.Add(OutputLine.Out(""));

            Dictionary<string, List<string>> byTopic = Sort();
            var map = new List<string>();

            foreach ((string topic, string[] _) in HelpTopics.Groups)
                if (byTopic.TryGetValue(topic, out List<string> words)) map.Add($"{topic} {words.Count}");

            foreach (KeyValuePair<string, List<string>> pair in byTopic)
                if (!HelpTopics.IsTopic(pair.Key)) map.Add($"{pair.Key} {pair.Value.Count}");

            if (byTopic.TryGetValue(HelpTopics.Terminal, out List<string> mine))
                map.Add($"{HelpTopics.Terminal} {mine.Count}");

            Wrap("topics", map, lines);

            lines.Add(OutputLine.Dim($"{Total()} commands. 'help <topic>' lists one, 'help <command>' explains one, "
                                     + "'help all' lists every one."));
        }

        /// <summary>One group, every command in it, one per line.</summary>
        private void OneTopic(string topic, List<OutputLine> lines)
        {
            Dictionary<string, List<string>> byTopic = Sort();

            if (!byTopic.TryGetValue(topic, out List<string> words) || words.Count == 0)
            {
                lines.Add(OutputLine.Warn($"help: nothing is filed under '{topic}' in this game"));
                return;
            }

            words.Sort(StringComparer.OrdinalIgnoreCase);

            lines.Add(OutputLine.Dim($"{topic} - {words.Count} command(s)"));
            foreach (string word in words)
            {
                CommandInfo command = _suggestions.Find(word);
                if (command != null) lines.Add(OutputLine.Out(Row(command)));
            }
        }

        /// <summary>
        /// `help all`, a page at a time.
        ///
        /// Cut into pages because the terminal draws eighteen lines and would otherwise show only the tail. Cut at
        /// HEADINGS rather than at a line count: a page that opens with the second half of `graphics` reads as a
        /// list of commands belonging to nothing.
        /// </summary>
        private void Everything(int page, List<OutputLine> lines)
        {
            Dictionary<string, List<string>> byTopic = Sort();
            var blocks = new List<List<OutputLine>>();

            foreach ((string topic, string[] _) in HelpTopics.Groups)
                if (byTopic.TryGetValue(topic, out List<string> words)) blocks.Add(TopicBlock(topic, words));

            // Then every mod's own commands, then anything unclassified. A mod gets a heading of its own rather
            // than being folded into "other" - who added a command is the thing you most want to know about one you
            // have never seen.
            foreach (KeyValuePair<string, List<string>> pair in byTopic)
            {
                if (HelpTopics.IsTopic(pair.Key) || pair.Key == HelpTopics.Other) continue;

                blocks.Add(TopicBlock(pair.Key, pair.Value));
            }

            if (byTopic.TryGetValue(HelpTopics.Terminal, out List<string> mine))
                blocks.Add(TopicBlock(HelpTopics.Terminal, mine));

            if (byTopic.TryGetValue(HelpTopics.Other, out List<string> rest))
                blocks.Add(TopicBlock(HelpTopics.Other, rest));

            // The terminal draws eighteen lines; one goes to the echoed command and one to the footer.
            const int PerPage = 16;
            var pages = new List<List<OutputLine>> { new List<OutputLine>() };

            foreach (List<OutputLine> block in blocks)
            {
                List<OutputLine> current = pages[pages.Count - 1];

                if (current.Count > 0 && current.Count + block.Count > PerPage)
                {
                    current = new List<OutputLine>();
                    pages.Add(current);
                }

                current.AddRange(block);
            }

            if (page > pages.Count) page = pages.Count;
            lines.AddRange(pages[page - 1]);

            lines.Add(OutputLine.Dim(page < pages.Count
                ? $"page {page} of {pages.Count} - 'help all {page + 1}' for the next."
                : $"{Total()} commands in all."));
        }

        private List<OutputLine> TopicBlock(string topic, List<string> words)
        {
            var block = new List<OutputLine> { OutputLine.Dim(topic) };
            words.Sort(StringComparer.OrdinalIgnoreCase);

            foreach (string word in words)
            {
                CommandInfo command = _suggestions.Find(word);
                if (command != null) block.Add(OutputLine.Out("  " + Row(command)));
            }

            return block;
        }

        /// <summary>A label and a run of short words wrapped under it - the topic map, and nothing else. Anything
        /// with a meaning to explain gets a line of its own instead.</summary>
        private static void Wrap(string label, List<string> words, List<OutputLine> lines)
        {
            if (words.Count == 0) return;

            const int Label = 10;
            const int Width = 96;

            var row = new System.Text.StringBuilder(Pad(label, Label));
            int used = Label;

            foreach (string word in words)
            {
                if (used > Label && used + 3 + word.Length > Width)
                {
                    lines.Add(OutputLine.Out(row.ToString()));
                    row = new System.Text.StringBuilder(new string(' ', Label));
                    used = Label;
                }

                if (used > Label) { row.Append("   "); used += 3; }

                row.Append(word);
                used += word.Length;
            }

            lines.Add(OutputLine.Out(row.ToString()));
        }

        /// <summary>Every command word under its heading: the game's by topic, this terminal's under "terminal", a
        /// mod's under the mod's own name.</summary>
        private Dictionary<string, List<string>> Sort()
        {
            var byTopic = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (CommandInfo command in _catalogue.Commands)
                Put(byTopic, command.IsVanilla ? HelpTopics.TopicOf(command.Word) : Mod(command.Source),
                    command.Word);

            foreach (CommandInfo builtin in Catalogue)
                if (!_suggestions.IsCommand(builtin.Word)) Put(byTopic, HelpTopics.Terminal, builtin.Word);

            return byTopic;
        }

        /// <summary>A source label as a heading: "Litterally v1.1.0" is a version, and the version belongs in
        /// `help &lt;word&gt;` rather than across the top of a list.</summary>
        private static string Mod(string source)
        {
            if (string.IsNullOrEmpty(source)) return HelpTopics.Other;

            int space = source.LastIndexOf(" v", StringComparison.Ordinal);
            return space > 0 ? source.Substring(0, space) : source;
        }

        private static void Put(Dictionary<string, List<string>> byTopic, string topic, string word)
        {
            if (!byTopic.TryGetValue(topic, out List<string> words)) byTopic[topic] = words = new List<string>();

            words.Add(word);
        }

        private int Total()
        {
            int count = _catalogue.Commands.Count;

            foreach (CommandInfo builtin in Catalogue)
                if (!_suggestions.IsCommand(builtin.Word)) count++;

            return count;
        }

        /// <summary>
        /// The `usage:` line under an error, which is the shape the player got wrong.
        ///
        /// Two lines rather than one long sentence, because that is what a shell prints and because the two answer
        /// different questions - what went wrong, and what it should have looked like.
        /// </summary>
        private static void UsageLine(string word, List<OutputLine> lines)
        {
            foreach (CommandInfo builtin in Catalogue)
                if (string.Equals(builtin.Word, word, StringComparison.OrdinalIgnoreCase))
                {
                    lines.Add(OutputLine.Dim("usage: " + builtin.Signature));
                    return;
                }
        }

        /// <summary>
        /// A description as the second column of a table rather than as prose: lower case, no full stop.
        ///
        /// "Short description fragments in the second column of Options or Commands should begin with a lowercase
        /// letter and are not expected to be full sentences" - and the game writes its own as sentences, so a list
        /// mixing the two looks like two lists. An all-caps word is left alone: NPCs is not a sentence opener.
        /// </summary>
        private static string Fragment(string description)
        {
            if (string.IsNullOrEmpty(description)) return "";

            string text = description.TrimEnd();
            if (text.EndsWith(".", StringComparison.Ordinal) && !text.EndsWith("..", StringComparison.Ordinal))
                text = text.Substring(0, text.Length - 1);

            if (text.Length > 1 && char.IsUpper(text[0]) && !char.IsUpper(text[1]))
                text = char.ToLowerInvariant(text[0]) + text.Substring(1);

            return text;
        }

        private static void Detail(CommandInfo command, List<OutputLine> lines)
        {
            lines.Add(OutputLine.Out(command.Signature));

            if (command.Description.Length > 0) lines.Add(OutputLine.Out("  " + Fragment(command.Description)));
            if (command.Usage.Length > 0) lines.Add(OutputLine.Dim("  e.g. " + command.Usage));

            lines.Add(OutputLine.Dim("  " + command.Source));
        }

        /// <summary>
        /// One command as a row: the word, then what it does, starting at a fixed column.
        ///
        /// Two columns because that is what a reader scans - down the left for the name they half-remember, across
        /// for the one they are checking. The description is clipped rather than wrapped: a second line under a
        /// command reads as another command at a glance, and the whole sentence is one `help &lt;word&gt;` away.
        /// </summary>
        private static string Row(CommandInfo command)
        {
            const int Column = 24;
            const int Width = 96;

            string what = command.Description.Length > 0 ? Fragment(command.Description) : command.Signature;
            return Pad(command.Word, Column) + Markup.Clip(what, Width - Column);
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
                lines.Add(OutputLine.Dim(query.Length == 0 ? "history: nothing yet"
                                              : $"history: nothing matches '{query}'"));
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
                if (_aliases.Count == 0) { lines.Add(OutputLine.Dim("no aliases yet - alias <name> <command> makes one")); return; }

                foreach (KeyValuePair<string, string> pair in _aliases.All)
                    lines.Add(OutputLine.Out(Pad(pair.Key, 16) + pair.Value));

                return;
            }

            if (tokens.Count < 3)
            {
                lines.Add(OutputLine.Error("alias: needs a name and a command"));
                UsageLine("alias", lines);
                lines.Add(OutputLine.Dim("       quote the command if it has spaces"));
                return;
            }

            string name = tokens[1];
            string expansion = rest.Substring(name.Length).Trim();

            // IsKnown rather than IsCommand: `alias help "give ogkush 5"` would otherwise take help away, and the day
            // the player forgets they did that is the day they cannot look up why.
            if (!_aliases.TrySet(name, expansion, _suggestions.IsKnown, out string error))
            {
                lines.Add(OutputLine.Error(error));
                return;
            }

            lines.Add(OutputLine.Out($"{name} -> {expansion}"));
        }

        private void Unalias(string rest, List<OutputLine> lines)
        {
            if (rest.Length == 0) { lines.Add(OutputLine.Error("unalias: needs a name")); UsageLine("unalias", lines); return; }

            lines.Add(_aliases.Remove(rest)
                ? OutputLine.Out($"{rest} removed.")
                : OutputLine.Warn($"unalias: no alias called '{rest}'"));
        }

        // ------------------------------------------------------------------------------------- grep and copy --

        private void Grep(string pattern, List<OutputLine> lines)
        {
            if (pattern.Length == 0)
            {
                lines.Add(OutputLine.Error("grep: needs something to look for"));
                UsageLine("grep", lines);
                return;
            }

            var hits = new List<OutputLine>();
            foreach (OutputLine line in _transcript.Lines)
                if (line.Text.IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(line);

            if (hits.Count == 0) { lines.Add(OutputLine.Dim($"grep: nothing on screen matches '{pattern}'")); return; }

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

            if (string.IsNullOrEmpty(value)) { lines.Add(OutputLine.Warn("copy: nothing on screen to copy")); return; }

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
