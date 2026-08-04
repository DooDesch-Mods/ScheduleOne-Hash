using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// Lines the player has run, newest first, kept across sessions.
    ///
    /// The console autocomplete mod held twenty-five of these in memory and lost them on every restart, which is the
    /// single most annoying thing about the vanilla console: the command you spent a minute getting right is gone
    /// the next time you play. This one is written to disk, globally rather than beside a save - the commands you
    /// type describe you, not the world.
    /// </summary>
    public sealed class History
    {
        private const string FileName = "history.txt";

        /// <summary>Kept lines. Large enough that a session's worth survives, small enough that the file stays a few
        /// kilobytes and a reverse search never has to think about it.</summary>
        public const int Capacity = 500;

        private readonly List<string> _lines = new();

        private bool _dirty;

        /// <summary>Newest first.</summary>
        public IReadOnlyList<string> Lines => _lines;

        public int Count => _lines.Count;

        /// <summary>
        /// Remember a line that was run.
        ///
        /// A repeat moves to the front rather than being added again: a history where `give ogkush 5` fills nine of
        /// the last ten rows is a history you cannot walk.
        /// </summary>
        public void Add(string line)
        {
            string one = line?.Trim();
            if (string.IsNullOrEmpty(one)) return;

            _lines.RemoveAll(existing => string.Equals(existing, one, StringComparison.Ordinal));
            _lines.Insert(0, one);

            if (_lines.Count > Capacity) _lines.RemoveRange(Capacity, _lines.Count - Capacity);

            _dirty = true;
        }

        /// <summary>Every line containing what was typed, newest first. An empty query returns everything, which is
        /// what makes Up on an empty prompt walk the whole history.</summary>
        public List<string> Search(string query)
        {
            var hits = new List<string>();
            if (string.IsNullOrEmpty(query)) { hits.AddRange(_lines); return hits; }

            foreach (string line in _lines)
                if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0) hits.Add(line);

            return hits;
        }

        /// <summary>
        /// The most recent line containing what was typed - Ctrl+R.
        ///
        /// <paramref name="skip"/> walks further back, so pressing Ctrl+R again reaches the one before. Returns null
        /// once there is nothing older, which is where the search stops rather than wrapping: wrapping a reverse
        /// search silently hands back a line the player already rejected.
        /// </summary>
        public string ReverseSearch(string query, int skip)
        {
            if (skip < 0) return null;

            List<string> hits = Search(query);
            return skip < hits.Count ? hits[skip] : null;
        }

        public void Clear()
        {
            if (_lines.Count == 0) return;

            _lines.Clear();
            _dirty = true;
        }

        public void Load(IStore store)
        {
            _lines.Clear();
            _dirty = false;

            string text = store?.Read(StoreScope.Global, FileName);
            if (string.IsNullOrEmpty(text)) return;

            foreach (string line in text.Split('\n'))
            {
                string one = line.Trim('\r');
                if (one.Length == 0) continue;

                _lines.Add(one);
                if (_lines.Count >= Capacity) break;
            }
        }

        public void Save(IStore store)
        {
            if (!_dirty || store == null) return;

            var sb = new StringBuilder();
            foreach (string line in _lines) sb.Append(line).Append('\n');

            store.Write(StoreScope.Global, FileName, sb.ToString());
            _dirty = false;
        }
    }
}
