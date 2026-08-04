namespace Hash.Terminal
{
    /// <summary>What became of a line after its context words were replaced.</summary>
    public readonly struct Expansion
    {
        internal Expansion(string line, string error)
        {
            Line = line ?? "";
            Error = error;
        }

        /// <summary>The line with every mark replaced by an id, ready to run.</summary>
        public string Line { get; }

        /// <summary>Why nothing will run, or null. Already shaped as terminal output: first line the problem,
        /// further lines the explanation.</summary>
        public string Error { get; }

        public bool Failed => Error != null;
    }

    /// <summary>
    /// Turning `setrelationship # 5` into `setrelationship jessi_waters 5`.
    ///
    /// Three ways this can go, and only the first one runs anything:
    ///
    ///   the mark exists and fits    -> the id goes in
    ///   the mark exists, wrong kind -> refused, saying what is marked and what the command wanted
    ///   nothing is marked           -> refused, saying so
    ///
    /// There is deliberately no fourth. Substituting something nearby that WOULD fit is the one behaviour that
    /// would make the feature untrustworthy: the player would have no way to know, from the line they typed, which
    /// NPC they just set to 5. A refusal costs a keystroke; a wrong target costs the feature.
    /// </summary>
    public sealed class MarkExpansion
    {
        private readonly Marks _marks;
        private readonly ICommandCatalogue _catalogue;

        public MarkExpansion(Marks marks, ICommandCatalogue catalogue)
        {
            _marks = marks;
            _catalogue = catalogue;
        }

        /// <summary>
        /// Replace every context word in one statement.
        ///
        /// Works on a statement rather than a whole line, so each half of `give # 1 ; teleport #` is checked against
        /// the command it belongs to.
        /// </summary>
        public Expansion Apply(string statement)
        {
            if (string.IsNullOrEmpty(statement) || statement.IndexOf(Marks.Sigil) < 0)
                return new Expansion(statement, null);

            List<string> tokens = CommandLine.Tokenise(statement);
            if (tokens.Count == 0) return new Expansion(statement, null);

            string command = tokens[0];
            var rebuilt = new System.Text.StringBuilder(command);

            // `alias t teleport #` writes a line down for later, so its `#` must stay a `#` - resolved every time
            // the alias runs, not once when it was defined. Quoting it already had that effect; without quotes it
            // did not, and the same line meant two different things depending on punctuation.
            int stored = CommandLine.StoredCommandAt(statement);

            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];
                rebuilt.Append(' ');

                if (stored >= 0 && i >= stored) { rebuilt.Append(Quote(token)); continue; }
                if (!Marks.IsWord(token)) { rebuilt.Append(Quote(token)); continue; }

                Mark mark = _marks.Resolve(token);
                int argIndex = i - 1;

                if (!mark.Exists) return new Expansion(null, Unmarked(token));

                MarkKind wanted = _catalogue.KindOf(command, argIndex);
                if (!Fits(mark.Kind, wanted)) return new Expansion(null, Mismatch(command, token, mark, wanted));

                rebuilt.Append(Quote(mark.Id));
            }

            return new Expansion(rebuilt.ToString(), null);
        }

        /// <summary>
        /// Whether a mark of this kind may be used where that kind is wanted.
        ///
        /// <see cref="MarkKind.Any"/> is a wildcard on BOTH sides, and for two different reasons. A mark that is Any
        /// came from a value a command already accepted, so it has earned the benefit of the doubt. An argument that
        /// wants Any takes several kinds by design - `teleport` goes to a place, a property or a person, and sorting
        /// out which is the command's own job.
        /// </summary>
        private static bool Fits(MarkKind have, MarkKind wanted)
        {
            if (wanted == MarkKind.None) return false;
            if (have == MarkKind.Any || wanted == MarkKind.Any) return true;

            return have == wanted;
        }

        private static string Unmarked(string word) => word == "#"
            ? "#: nothing marked - look at something before opening the terminal"
            : $"{word}: nothing there right now";

        private static string Mismatch(string command, string word, Mark mark, MarkKind wanted)
        {
            string first = $"{word}: marked {Mark.Word(mark.Kind)} '{mark.Id}'";

            return wanted == MarkKind.None
                ? first + $"\n{command} does not take one there"
                : first + $"\n{command} needs {AnA(Mark.Word(wanted))} there";
        }

        /// <summary>
        /// "a" or "an", by how the word is SAID rather than how it is spelled.
        ///
        /// `npc` is read out as three letters, and the first of them starts with a vowel sound - "an N-P-C". A rule
        /// that only looks at the letter writes "a npc", which every reader trips over.
        /// </summary>
        private static string AnA(string word)
        {
            if (word.Length == 0) return word;
            if (string.Equals(word, "npc", StringComparison.OrdinalIgnoreCase)) return "an " + word;

            return "aeiou".IndexOf(char.ToLowerInvariant(word[0])) >= 0 ? "an " + word : "a " + word;
        }

        /// <summary>An id with a space in it has to go back in quoted, or the game reads it as two arguments. Rare,
        /// but property names can carry one.</summary>
        private static string Quote(string value) =>
            value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value;
    }
}
