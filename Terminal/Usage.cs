using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// How often this save has used each command and each argument, and the ordering that falls out of it.
    ///
    /// The rule that matters is the one that is easy to get wrong: <b>usage only reorders candidates that matched
    /// equally well.</b> A close hit must never sink below a loose one just because the loose one is popular - type
    /// `ogk` after using `give` a hundred times and `ogkush` still has to be first. Sorting by count alone produces
    /// a list that is right until the moment you need it.
    /// </summary>
    public sealed class Usage
    {
        private const string FileName = "usage.txt";

        /// <summary>Only the first positional argument is counted. The second is usually a quantity, and learning
        /// that the player likes the number 5 helps nobody.</summary>
        private const int CountedArgIndex = 0;

        private readonly Dictionary<string, int> _commands = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _args = new(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;

        /// <summary>Note that a line was run. Takes the already-parsed tokens so the counting rule cannot drift from
        /// the parsing rule.</summary>
        public void Record(IReadOnlyList<string> tokens)
        {
            if (tokens == null || tokens.Count == 0) return;

            string word = tokens[0];
            if (string.IsNullOrWhiteSpace(word)) return;

            Bump(_commands, word);

            if (tokens.Count > CountedArgIndex + 1)
                Bump(_args, Key(word, tokens[CountedArgIndex + 1]));

            _dirty = true;
        }

        public int CommandCount(string word) =>
            word != null && _commands.TryGetValue(word, out int n) ? n : 0;

        public int ArgCount(string command, string value) =>
            command != null && value != null && _args.TryGetValue(Key(command, value), out int n) ? n : 0;

        /// <summary>
        /// Order a set of matched candidates: match quality first, then how often this save used it, then
        /// alphabetically so the list never shuffles between keystrokes for no reason.
        ///
        /// <paramref name="countOf"/> is supplied by the caller because a command and an argument count differently
        /// and the comparison should not have to know which it is looking at.
        /// </summary>
        public static void Order(List<Suggestion> candidates, Func<Suggestion, int> countOf)
        {
            if (candidates == null || candidates.Count < 2) return;

            candidates.Sort((a, b) =>
            {
                if (a.Score != b.Score) return b.Score.CompareTo(a.Score);

                int used = countOf(b).CompareTo(countOf(a));
                if (used != 0) return used;

                return string.Compare(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);
            });
        }

        /// <summary>
        /// Group argument candidates by who supplies them: the game first, then one block per mod, alphabetically,
        /// with anything unattributable last.
        ///
        /// Stable within each block, so the ordering above survives. The point is that a player looking for a vanilla
        /// item is not made to read through another mod's four hundred additions first, and a player looking for a
        /// mod's item finds them together instead of scattered.
        /// </summary>
        public static List<Suggestion> GroupBySupplier(List<Suggestion> ordered)
        {
            if (ordered == null || ordered.Count < 2) return ordered ?? new List<Suggestion>();

            var game = new List<Suggestion>();
            var unknown = new List<Suggestion>();
            var byMod = new SortedDictionary<string, List<Suggestion>>(StringComparer.OrdinalIgnoreCase);

            foreach (Suggestion s in ordered)
            {
                if (s.IsVanilla) { game.Add(s); continue; }
                if (string.IsNullOrEmpty(s.Source)) { unknown.Add(s); continue; }

                if (!byMod.TryGetValue(s.Source, out List<Suggestion> block))
                    byMod[s.Source] = block = new List<Suggestion>();

                block.Add(s);
            }

            var result = new List<Suggestion>(ordered.Count);
            result.AddRange(game);
            foreach (KeyValuePair<string, List<Suggestion>> pair in byMod) result.AddRange(pair.Value);
            result.AddRange(unknown);
            return result;
        }

        // ------------------------------------------------------------------------------------------ storage --

        /// <summary>
        /// Load this save's counts.
        ///
        /// The format is two columns of text rather than JSON: it is read and written by nothing but this class, a
        /// truncated last line costs one count instead of the whole file, and a player who opens it can see what the
        /// mod remembers about them.
        /// </summary>
        public void Load(IStore store)
        {
            _commands.Clear();
            _args.Clear();
            _dirty = false;

            string text = store?.Read(StoreScope.Save, FileName);
            if (string.IsNullOrEmpty(text)) return;

            foreach (string line in text.Split('\n'))
            {
                string one = line.Trim('\r', ' ');
                if (one.Length == 0) continue;

                int tab = one.LastIndexOf('\t');
                if (tab <= 0 || !int.TryParse(one.Substring(tab + 1), out int count) || count <= 0) continue;

                string key = one.Substring(0, tab);

                // An argument key carries the command it belongs to, separated by a space; a command key never has
                // one, because a command word cannot contain a space.
                if (key.IndexOf(' ') > 0) _args[key] = count;
                else _commands[key] = count;
            }
        }

        public void Save(IStore store)
        {
            if (!_dirty || store == null) return;

            var sb = new StringBuilder();
            foreach (KeyValuePair<string, int> pair in _commands) sb.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');
            foreach (KeyValuePair<string, int> pair in _args) sb.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');

            store.Write(StoreScope.Save, FileName, sb.ToString());
            _dirty = false;
        }

        private static string Key(string command, string value) => command + " " + value;

        private static void Bump(Dictionary<string, int> into, string key)
        {
            into.TryGetValue(key, out int n);
            into[key] = n + 1;
        }
    }
}
