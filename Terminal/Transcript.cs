namespace Hash.Terminal
{
    /// <summary>
    /// What the terminal has printed, oldest first.
    ///
    /// Two limits, and they answer different questions. <see cref="Kept"/> is how far back `grep` and the scrollback
    /// can reach; <see cref="Shown"/> is how many lines are handed to the page. They are far apart on purpose: the
    /// renderer rebuilds the whole page for any text change, so what it draws has to stay small, while what is
    /// remembered costs a string each and may as well be a session's worth.
    /// </summary>
    public sealed class Transcript
    {
        /// <summary>Lines remembered. A `logs` stream can fill this in a minute, which is why there is a cap at
        /// all.</summary>
        public const int Kept = 2000;

        /// <summary>Lines handed to the page. Roughly what fits the landscape viewport; the rest has scrolled out of
        /// sight and drawing it would cost a rebuild for nothing.</summary>
        public const int Shown = 40;

        private readonly List<OutputLine> _lines = new();

        public IReadOnlyList<OutputLine> Lines => _lines;

        public int Count => _lines.Count;

        public void Add(OutputLine line)
        {
            _lines.Add(line);

            // Trimmed in blocks rather than one at a time: removing the first element of a list is a copy of
            // everything after it, and a log stream would do that once per line.
            if (_lines.Count > Kept + 256) _lines.RemoveRange(0, _lines.Count - Kept);
        }

        public void Add(IEnumerable<OutputLine> lines)
        {
            if (lines == null) return;

            foreach (OutputLine line in lines) Add(line);
        }

        public void Clear() => _lines.Clear();

        /// <summary>The last <paramref name="count"/> lines, newest first - what `copy` walks.</summary>
        public IEnumerable<OutputLine> Recent(int count)
        {
            for (int i = _lines.Count - 1; i >= 0 && count > 0; i--, count--) yield return _lines[i];
        }

        /// <summary>The tail the page draws, oldest first.</summary>
        public List<OutputLine> Window()
        {
            int from = Math.Max(0, _lines.Count - Shown);
            return _lines.GetRange(from, _lines.Count - from);
        }
    }
}
