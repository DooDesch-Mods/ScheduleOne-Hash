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

            // Only the last statement matters: after `settime 1200 ; give ` the player is completing `give`.
            string tail = LastStatement(line);

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

            foreach (KeyValuePair<string, string> alias in _aliases.All)
            {
                MatchResult hit = FuzzyMatcher.Match(alias.Key, prefix);
                if (!hit.IsMatch) continue;

                rows.Add(new Suggestion(SuggestionKind.Command, alias.Key, "Alias", false, hit.Score));
            }

            Usage.Order(rows, s => _usage.CommandCount(s.Value));
            Trim(rows, MaxMatches);

            // History goes on the end rather than competing for the visible rows: it answers a different question,
            // and a line you ran before should never push the command you are typing off the list.
            rows.AddRange(HistoryRows(wholeLine, MaxHistoryRows));

            return new SuggestionSet(rows, null, -1, prefix);
        }

        // ---------------------------------------------------------------------------------------- arguments --

        private SuggestionSet ForArgument(string statement, int tokenIndex, string prefix)
        {
            List<string> tokens = CommandLine.Tokenise(statement);
            string word = tokens.Count > 0 ? tokens[0] : "";

            CommandInfo command = Find(word);
            int argIndex = tokenIndex - 1;

            var rows = new List<Suggestion>();

            foreach (ArgValue value in _catalogue.ValuesFor(word, argIndex))
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

        public CommandInfo Find(string word)
        {
            if (string.IsNullOrEmpty(word)) return null;

            foreach (CommandInfo command in _catalogue.Commands)
                if (string.Equals(command.Word, word, StringComparison.OrdinalIgnoreCase)) return command;

            return null;
        }

        public bool IsCommand(string word) => Find(word) != null;

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
