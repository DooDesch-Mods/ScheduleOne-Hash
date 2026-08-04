namespace Hash.Terminal
{
    /// <summary>What the terminal is currently offering, and what it knows about the line that produced it.</summary>
    public sealed class SuggestionSet
    {
        internal SuggestionSet(IReadOnlyList<Suggestion> rows, CommandInfo command, int argIndex, string prefix)
        {
            Rows = rows ?? Array.Empty<Suggestion>();
            Command = command;
            ArgIndex = argIndex;
            Prefix = prefix ?? "";
        }

        public static readonly SuggestionSet Empty = new SuggestionSet(null, null, -1, "");

        public IReadOnlyList<Suggestion> Rows { get; }

        /// <summary>The command the line names, once it names one - what the header and the description come from.
        /// Null while the player is still typing the first word.</summary>
        public CommandInfo Command { get; }

        /// <summary>Which argument the caret sits in, or -1 while it is still in the command word.</summary>
        public int ArgIndex { get; }

        /// <summary>What has been typed of the token under the caret.</summary>
        public string Prefix { get; }

        public bool Any => Rows.Count > 0;
    }

    /// <summary>
    /// Turning a half-typed line into the list under the prompt.
    ///
    /// Three sources feed it and the order between them is deliberate. Commands or argument values come first,
    /// because they are what the player is reaching for. History comes last, because it answers a different
    /// question - "what did I run before" - and a history line that outranked the item you are typing would be
    /// infuriating. Within a source, match quality outranks how often this save used it.
    /// </summary>
    public sealed class Suggestions
    {
        /// <summary>Rows DRAWN at once. The block sits between the transcript and the prompt and pushes the
        /// transcript up, so a taller list costs the player the thing they are reading.</summary>
        public const int MaxRows = 8;

        /// <summary>
        /// Matches KEPT, which is a different number and the reason the list is worth walking.
        ///
        /// Only <see cref="MaxRows"/> are drawn, but the selection moves through all of these and the window follows
        /// it - so an empty prompt really does let you read every command the game has, eight at a time, instead of
        /// wrapping around the same first eight forever.
        /// </summary>
        public const int MaxMatches = 300;

        /// <summary>History rows in a mixed list. A handful is a reminder; more would bury the real candidates.</summary>
        public const int MaxHistoryRows = 3;

        /// <summary>History rows on an untouched prompt, where nothing is competing with them - one screenful, so
        /// holding Up walks back through a session the way it does in a shell.</summary>
        public const int MaxHistoryRowsAlone = 8;

        private readonly ICommandCatalogue _catalogue;
        private readonly Usage _usage;
        private readonly History _history;
        private readonly Aliases _aliases;

        public Suggestions(ICommandCatalogue catalogue, Usage usage, History history, Aliases aliases)
        {
            _catalogue = catalogue;
            _usage = usage;
            _history = history;
            _aliases = aliases;
        }

        /// <summary>
        /// What to offer for this line.
        ///
        /// The caret is taken to be at the end, which is where it is while typing. A player who clicks into the
        /// middle of a line and keeps typing gets completions for the last token instead of the one they are in -
        /// the alternative needs a caret position the renderer does not report, and the common case is worth more
        /// than the rare one.
        /// </summary>
        public SuggestionSet For(string line)
        {
            if (line == null) line = "";

            // Only the last statement matters: after `settime 1200 ; give ` the player is completing `give`. And
            // inside that statement, only what will actually run: `repeat 5 give ` is completing `give`.
            string tail = CommandLine.Unwrap(LastStatement(line));

            CommandLine.TokenAtCaret(tail, out int tokenIndex, out string prefix);

            return tokenIndex == 0
                ? ForCommandWord(prefix, line)
                : ForArgument(tail, tokenIndex, prefix);
        }

        // ------------------------------------------------------------------------------------- command word --

        private SuggestionSet ForCommandWord(string prefix, string wholeLine)
        {
            var rows = new List<Suggestion>();

            foreach (CommandInfo command in _catalogue.Commands)
            {
                MatchResult hit = FuzzyMatcher.Match(command.Word, prefix);
                if (!hit.IsMatch) continue;

                rows.Add(new Suggestion(SuggestionKind.Command, command.Word, command.Source, command.IsVanilla, hit.Score));
            }

            // The terminal's own commands rank with the game's rather than after them. They are the only way to reach
            // `help` or `grep`, and a list that offered every command except the ones this mod added would be an odd
            // thing to ship.
            foreach (CommandInfo builtin in Builtins.Catalogue)
            {
                if (InCatalogue(builtin.Word) != null) continue;   // a game command of that name wins, and runs

                MatchResult hit = FuzzyMatcher.Match(builtin.Word, prefix);
                if (!hit.IsMatch) continue;

                rows.Add(new Suggestion(SuggestionKind.Command, builtin.Word, builtin.Source, false, hit.Score));
            }

            foreach (KeyValuePair<string, string> alias in _aliases.All)
            {
                MatchResult hit = FuzzyMatcher.Match(alias.Key, prefix);
                if (!hit.IsMatch) continue;

                rows.Add(new Suggestion(SuggestionKind.Command, alias.Key, "Alias", false, hit.Score));
            }

            Usage.Order(rows, s => _usage.CommandCount(s.Value));
            Trim(rows, MaxMatches);

            // History goes at the BOTTOM, oldest first - which puts the last thing you ran on the very last row.
            //
            // The block is drawn above the prompt and grows downwards, so the bottom row is the one nearest the
            // hand. Down from a closed list opens on the commands; Up opens on the last row, which is the command
            // you just ran; Up again is the one before it. Both arrows land where a shell puts them, and neither
            // needed a second meaning to get there.
            List<Suggestion> past = HistoryRows(wholeLine, prefix.Length == 0 ? MaxHistoryRowsAlone : MaxHistoryRows);
            past.Reverse();

            rows.AddRange(past);

            return new SuggestionSet(rows, null, -1, prefix);
        }

        // ---------------------------------------------------------------------------------------- arguments --

        private SuggestionSet ForArgument(string statement, int tokenIndex, string prefix)
        {
            List<string> tokens = CommandLine.Tokenise(statement);
            string word = tokens.Count > 0 ? tokens[0] : "";
            int argIndex = tokenIndex - 1;

            Resolve(ref word, ref argIndex);

            CommandInfo command = Find(word);

            var rows = new List<Suggestion>();

            foreach (ArgValue value in Values(word, argIndex))
            {
                MatchResult hit = FuzzyMatcher.Match(value.Value, prefix);
                if (!hit.IsMatch) continue;

                rows.Add(new Suggestion(SuggestionKind.Argument, value.Value, value.Source, value.IsVanilla, hit.Score));
            }

            // No provider claims this slot, so mine the game's own usage example for literal choices. That is where
            // `setweather <clear|lightrain|heavyrain>` comes from for commands nobody wrote a provider for.
            if (rows.Count == 0 && !_catalogue.Owns(word, argIndex) && command != null)
            {
                foreach (string literal in UsageExample.Literals(command.Usage, command.Signature, argIndex))
                {
                    MatchResult hit = FuzzyMatcher.Match(literal, prefix);
                    if (!hit.IsMatch) continue;

                    rows.Add(new Suggestion(SuggestionKind.Argument, literal, command.Source, command.IsVanilla, hit.Score));
                }
            }

            Usage.Order(rows, s => _usage.ArgCount(word, s.Value));
            rows = Usage.GroupBySupplier(rows);
            Trim(rows, MaxMatches);

            return new SuggestionSet(rows, command, argIndex, prefix);
        }

        /// <summary>
        /// The values for one argument: the game's, or the terminal's own.
        ///
        /// The terminal's commands take arguments too, and nothing in the game can know what they are - the aliases
        /// live here, the topics live here, the log filters live here. Left to the catalogue alone, `unalias` had a
        /// completion list of nothing while the answer sat two fields away.
        /// </summary>
        private IEnumerable<ArgValue> Values(string word, int argIndex)
        {
            IReadOnlyList<ArgValue> fromGame = _catalogue.ValuesFor(word, argIndex);
            if (fromGame.Count > 0) return fromGame;

            if (argIndex != 0) return fromGame;

            switch (word.ToLowerInvariant())
            {
                case "unalias": return Aliases();
                case "help": return Topics();
                case "logs": return Literals("off", "warn", "error", "log");
                default: return fromGame;
            }
        }

        private IEnumerable<ArgValue> Aliases()
        {
            foreach (KeyValuePair<string, string> alias in _aliases.All)
                yield return new ArgValue(alias.Key, "Alias", false);
        }

        /// <summary>What `help` takes: a topic, `all`, or any command word - and the words are already offered by the
        /// command list, so only the ones nothing else would name go here.</summary>
        private static IEnumerable<ArgValue> Topics()
        {
            yield return new ArgValue("all", Builtins.Source, false);

            foreach ((string topic, string[] _) in HelpTopics.Groups)
                yield return new ArgValue(topic, Builtins.Source, false);

            yield return new ArgValue(HelpTopics.Terminal, Builtins.Source, false);
        }

        private static IEnumerable<ArgValue> Literals(params string[] values)
        {
            foreach (string value in values) yield return new ArgValue(value, Builtins.Source, false);
        }

        /// <summary>
        /// Follow an alias to the command it stands for, and move the argument index by what it already filled in.
        ///
        /// Both halves are needed. Without the first, an alias completes nothing at all - which is backwards, since
        /// the whole point of `alias gk "give ogkush"` is to type it often. Without the second, the caret in `gk 5`
        /// would be offered items for what is actually the quantity.
        /// </summary>
        private void Resolve(ref string word, ref int argIndex)
        {
            string expansion = _aliases.Expand(word);
            if (string.IsNullOrEmpty(expansion)) return;

            List<string> parts = CommandLine.Tokenise(expansion);
            if (parts.Count == 0) return;

            word = parts[0];
            argIndex += parts.Count - 1;
        }

        // ------------------------------------------------------------------------------------------ history --

        private List<Suggestion> HistoryRows(string line, int budget)
        {
            var rows = new List<Suggestion>();
            if (budget <= 0) return rows;

            foreach (string past in _history.Search(line.Trim()))
            {
                // A line identical to what is already typed is not a suggestion.
                if (string.Equals(past, line.Trim(), StringComparison.Ordinal)) continue;

                rows.Add(new Suggestion(SuggestionKind.History, past, "History", false, 0));
                if (rows.Count >= budget) break;
            }

            return rows;
        }

        // -------------------------------------------------------------------------------------------- shared --

        /// <summary>Anything the terminal can describe: the game's commands and its own.</summary>
        public CommandInfo Find(string word) => InCatalogue(word) ?? InBuiltins(word);

        /// <summary>
        /// Whether the GAME registered this word.
        ///
        /// Narrower than <see cref="Find"/> on purpose, and the two must not be merged: this is what decides whether
        /// a builtin steps aside, so counting the builtins themselves would make every one of them refuse to run.
        /// </summary>
        public bool IsCommand(string word) => InCatalogue(word) != null;

        /// <summary>Whether anything already answers to this word - what an alias must not shadow.</summary>
        public bool IsKnown(string word) => Find(word) != null;

        private CommandInfo InCatalogue(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;

            foreach (CommandInfo command in _catalogue.Commands)
                if (string.Equals(command.Word, word, StringComparison.OrdinalIgnoreCase)) return command;

            return null;
        }

        private static CommandInfo InBuiltins(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;

            foreach (CommandInfo builtin in Builtins.Catalogue)
                if (string.Equals(builtin.Word, word, StringComparison.OrdinalIgnoreCase)) return builtin;

            return null;
        }

        private static void Trim(List<Suggestion> rows, int max)
        {
            if (max < 0) max = 0;
            if (rows.Count > max) rows.RemoveRange(max, rows.Count - max);
        }

        private static string LastStatement(string line)
        {
            int cut = -1;
            bool inQuotes = false;

            for (int i = 0; i < line.Length; i++)
            {
                if (line[i] == '"') inQuotes = !inQuotes;
                else if (line[i] == ';' && !inQuotes) cut = i;
            }

            return cut < 0 ? line : line.Substring(cut + 1).TrimStart();
        }
    }
}
