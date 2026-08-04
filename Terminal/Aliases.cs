using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// Short names the player gave to longer command lines.
    ///
    /// Three rules keep this from turning into a way to break the console:
    ///
    /// An alias never shadows a real command. `alias give ...` is refused, because the day the player forgets they
    /// did that is the day `give` stops working and nothing explains why.
    ///
    /// An alias is expanded exactly once. `alias gk "gk 5"` therefore runs the command `gk` and fails honestly
    /// instead of recursing until the frame dies.
    ///
    /// Aliases are global, not per save. A shortcut you built is yours, and starting a new game with an empty table
    /// reads as having lost them.
    /// </summary>
    public sealed class Aliases
    {
        private const string FileName = "aliases.txt";

        private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);

        private bool _dirty;

        public int Count => _byName.Count;

        /// <summary>Name to expansion, alphabetically - the order `alias` with no arguments prints.</summary>
        public IEnumerable<KeyValuePair<string, string>> All
        {
            get
            {
                var names = new List<string>(_byName.Keys);
                names.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string name in names) yield return new KeyValuePair<string, string>(name, _byName[name]);
            }
        }

        /// <summary>The line this name stands for, or null. Handed to the parser as its expansion callback.</summary>
        public string Expand(string name) =>
            name != null && _byName.TryGetValue(name, out string line) ? line : null;

        /// <summary>
        /// Define one. <paramref name="isRealCommand"/> decides what may not be shadowed; the check lives here so
        /// there is one place that knows the rule, and the caller supplies the catalogue.
        /// </summary>
        public bool TrySet(string name, string expansion, Func<string, bool> isRealCommand, out string error)
        {
            error = null;

            string key = name?.Trim();
            string value = expansion?.Trim();

            if (string.IsNullOrEmpty(key)) { error = "an alias needs a name."; return false; }
            if (string.IsNullOrEmpty(value)) { error = "an alias needs something to stand for."; return false; }

            if (key.IndexOf(' ') >= 0) { error = "an alias name cannot contain a space."; return false; }

            if (isRealCommand != null && isRealCommand(key))
            {
                error = $"'{key}' is a real command; an alias would hide it.";
                return false;
            }

            _byName[key] = value;
            _dirty = true;
            return true;
        }

        public bool Remove(string name)
        {
            if (name == null || !_byName.Remove(name)) return false;

            _dirty = true;
            return true;
        }

        public void Load(IStore store)
        {
            _byName.Clear();
            _dirty = false;

            string text = store?.Read(StoreScope.Global, FileName);
            if (string.IsNullOrEmpty(text)) return;

            foreach (string line in text.Split('\n'))
            {
                string one = line.Trim('\r');
                if (one.Length == 0) continue;

                int tab = one.IndexOf('\t');
                if (tab <= 0 || tab == one.Length - 1) continue;

                _byName[one.Substring(0, tab)] = one.Substring(tab + 1);
            }
        }

        public void Save(IStore store)
        {
            if (!_dirty || store == null) return;

            var sb = new StringBuilder();
            foreach (KeyValuePair<string, string> pair in All) sb.Append(pair.Key).Append('\t').Append(pair.Value).Append('\n');

            store.Write(StoreScope.Global, FileName, sb.ToString());
            _dirty = false;
        }
    }
}
