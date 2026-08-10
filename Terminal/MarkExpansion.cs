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
    /// Turning `setrelationship # 5` into `setrelationship jessi_waters 5`, and `give #hand 10-@` into
    /// `give granddaddypurple 5`.
    ///
    /// Two passes over one statement, in this order because the second needs the first: the marks become ids, and
    /// then the sums become numbers - `@` counts the stack of the item this statement names, and there is no item
    /// to name until `#hand` has been resolved.
    ///
    /// Three ways either pass can go, and only the first one runs anything:
    ///
    ///   the mark or sum works out   -> the value goes in
    ///   it works out to the wrong kind of thing -> refused, saying what was meant and what the command wanted
    ///   it does not work out at all -> refused, saying so
    ///
    /// There is deliberately no fourth. Substituting something nearby that WOULD fit is the one behaviour that
    /// would make the feature untrustworthy: the player would have no way to know, from the line they typed, which
    /// NPC they just set to 5. A refusal costs a keystroke; a wrong target costs the feature. The same reasoning
    /// covers a count nobody took - `@` on an item you are not carrying refuses rather than standing in a zero.
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
            if (!Interesting(statement)) return new Expansion(statement, null);

            List<string> tokens = CommandLine.Tokenise(statement);
            if (tokens.Count == 0) return new Expansion(statement, null);

            string command = tokens[0];

            // `alias t teleport #` writes a line down for later, so its `#` must stay a `#` - resolved every time
            // the alias runs, not once when it was defined. Quoting it already had that effect; without quotes it
            // did not, and the same line meant two different things depending on punctuation.
            int stored = CommandLine.StoredCommandAt(statement);

            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (stored >= 0 && i >= stored) continue;
                if (!Marks.IsWord(token)) continue;

                Mark mark = _marks.Resolve(token);
                int argIndex = i - 1;

                if (!mark.Exists) return new Expansion(null, Unmarked(token));

                MarkKind wanted = _catalogue.KindOf(command, argIndex);
                if (!Fits(mark.Kind, wanted)) return new Expansion(null, Mismatch(command, token, mark, wanted));

                tokens[i] = mark.Id;
            }

            // Second pass, because `@` counts the item this statement names and the marks have only just become
            // ids. `give #hand 10-@` has to know it is talking about granddaddypurple before it can count it.
            string error = Reckon(tokens, command, stored);
            if (error != null) return new Expansion(null, error);

            var rebuilt = new System.Text.StringBuilder(command);
            for (int i = 1; i < tokens.Count; i++) rebuilt.Append(' ').Append(Quote(tokens[i]));

            return new Expansion(rebuilt.ToString(), null);
        }

        /// <summary>
        /// Whether this statement is worth tokenising at all.
        ///
        /// Most lines are a word and an id and have nothing here to do. One scan for the two sigils and the
        /// operators is cheaper than a tokenise, and a false positive costs only the tokenise - the per-token gates
        /// below still decide what actually happens.
        /// </summary>
        private static bool Interesting(string statement)
        {
            if (string.IsNullOrEmpty(statement)) return false;

            foreach (char c in statement)
            {
                if (c == Marks.Sigil || c == Arithmetic.Sigil) return true;
                if (c == '+' || c == '-' || c == '*' || c == '/' || c == '(') return true;
            }

            return false;
        }

        /// <summary>
        /// Work out every token that is a sum rather than a value. Returns why nothing will run, or null.
        ///
        /// The stack behind <c>@</c> is counted once, and only when some token actually asks for it - the count is
        /// a walk over the hotbar, and most lines have no sum in them at all.
        /// </summary>
        private string Reckon(List<string> tokens, string command, int stored)
        {
            // Null means "not counted yet" and nothing else: a count that fails leaves through the return below,
            // so it can never be mistaken for one that has not happened.
            double? stack = null;

            for (int i = 1; i < tokens.Count; i++)
            {
                string token = tokens[i];

                if (stored >= 0 && i >= stored) continue;
                if (!Arithmetic.Looks(token)) continue;

                // A sum is a number, so it belongs where the command wants a number. Put one where a thing is
                // wanted and it would arrive as a bare count - `give @ 5` reading as `give 5 5`.
                MarkKind wanted = _catalogue.KindOf(command, i - 1);
                if (wanted != MarkKind.None)
                    return $"{token}: that is a sum\n{command} needs {AnA(Mark.Word(wanted))} there";

                if (Arithmetic.NeedsStack(token) && stack == null)
                {
                    stack = Count(tokens, command, out string why);

                    if (stack == null) return why;
                }

                Sum sum = Arithmetic.Evaluate(token, stack);
                if (sum.Failed) return sum.Error;

                tokens[i] = sum.Text;
            }

            return null;
        }

        /// <summary>
        /// How many <c>@</c> stands for: the stack of whichever item this statement names.
        ///
        /// The item argument when the command has one, and otherwise whatever is in the player's hand - which is
        /// what `setquantity @*2` means, since that command has no item argument and acts on the equipped item.
        /// </summary>
        private double? Count(List<string> tokens, string command, out string error)
        {
            string item = null;

            for (int i = 1; i < tokens.Count; i++)
            {
                if (_catalogue.KindOf(command, i - 1) != MarkKind.Item) continue;

                item = tokens[i];
                break;
            }

            if (string.IsNullOrEmpty(item)) item = _marks.Resolve("#hand").Id;

            if (string.IsNullOrEmpty(item))
            {
                error = "@: nothing in your hand to count\nname an item, or hold one";
                return null;
            }

            int held = _marks.Stack(item);

            if (held < 0)
            {
                error = $"@: you are not carrying any {item}";
                return null;
            }

            error = null;
            return held;
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
