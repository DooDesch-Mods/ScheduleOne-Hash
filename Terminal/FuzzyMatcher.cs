namespace Hash.Terminal
{
    /// <summary>How well a candidate answers what the player typed. Higher wins.</summary>
    public enum MatchKind
    {
        None = 0,

        /// <summary>Every typed character shows up in order, but not next to each other - `fzr` finds
        /// `fertilizer`.</summary>
        Subsequence = 1,

        /// <summary>The typed text appears somewhere inside the candidate.</summary>
        Substring = 2,

        /// <summary>The typed text starts one of the candidate's words - `fertilizer` finds
        /// `long_life_fertilizer`.</summary>
        WordStart = 3,

        /// <summary>The candidate starts with the typed text.</summary>
        Prefix = 4,

        /// <summary>The candidate is exactly what was typed.</summary>
        Exact = 5,
    }

    public readonly struct MatchResult
    {
        internal MatchResult(MatchKind kind, int offset)
        {
            Kind = kind;
            Offset = offset;
        }

        public MatchKind Kind { get; }

        /// <summary>Where the match starts. Breaks ties inside a kind: a hit near the front of the word reads as
        /// closer than the same hit buried in the middle.</summary>
        public int Offset { get; }

        public bool IsMatch => Kind != MatchKind.None;

        /// <summary>
        /// Kind and offset folded into one comparable number, so ranking can sort by a single value and still keep
        /// usage counts from crossing a band. Offset only ever moves a candidate WITHIN its kind - the multiplier is
        /// far larger than any word length.
        /// </summary>
        public int Score => Kind == MatchKind.None ? 0 : ((int)Kind * 1000) - Math.Min(Offset, 999);

        public static readonly MatchResult NoMatch = new MatchResult(MatchKind.None, int.MaxValue);
    }

    /// <summary>
    /// Graded matching for command words and argument values.
    ///
    /// Five grades rather than a yes/no, because a console has a thousand item ids and the difference between the
    /// one you meant and the nine hundred that merely contain the same letters is the whole product. `give
    /// fertilizer` has to find `long_life_fertilizer`, and `give fzr` still has to find something, but neither may
    /// push an exact hit off the top of the list.
    /// </summary>
    public static class FuzzyMatcher
    {
        /// <summary>Below this, a subsequence match is noise: one letter matches nearly everything.</summary>
        private const int MinSubsequenceQuery = 2;

        private static readonly char[] WordSeparators = { '_', '-', '.', ' ', '/', ':' };

        public static MatchResult Match(string candidate, string query)
        {
            if (string.IsNullOrEmpty(candidate)) return MatchResult.NoMatch;

            // Nothing typed yet: everything matches equally and the order falls to usage.
            if (string.IsNullOrEmpty(query)) return new MatchResult(MatchKind.Prefix, 0);

            if (candidate.Equals(query, StringComparison.OrdinalIgnoreCase))
                return new MatchResult(MatchKind.Exact, 0);

            if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return new MatchResult(MatchKind.Prefix, 0);

            int index = candidate.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (index > 0)
                return new MatchResult(IsWordStart(candidate, index) ? MatchKind.WordStart : MatchKind.Substring, index);

            if (query.Length >= MinSubsequenceQuery && TryMatchSubsequence(candidate, query, out int firstIndex))
                return new MatchResult(MatchKind.Subsequence, firstIndex);

            return MatchResult.NoMatch;
        }

        public static bool IsMatch(string candidate, string query) => Match(candidate, query).IsMatch;

        private static bool IsWordStart(string candidate, int index)
        {
            if (index <= 0) return true;

            char previous = candidate[index - 1];
            for (int i = 0; i < WordSeparators.Length; i++)
                if (previous == WordSeparators[i]) return true;

            // A camelCase boundary counts too: `spawnVehicle` matched at `Vehicle` is a word start, not a substring.
            return char.IsLower(previous) && char.IsUpper(candidate[index]);
        }

        private static bool TryMatchSubsequence(string candidate, string query, out int firstIndex)
        {
            firstIndex = -1;
            int q = 0;

            for (int i = 0; i < candidate.Length && q < query.Length; i++)
            {
                if (char.ToLowerInvariant(candidate[i]) != char.ToLowerInvariant(query[q])) continue;

                if (q == 0) firstIndex = i;
                q++;
            }

            if (q < query.Length) { firstIndex = -1; return false; }
            return true;
        }
    }
}
