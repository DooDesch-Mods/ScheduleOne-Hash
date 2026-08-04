namespace Hash.Terminal
{
    /// <summary>What a navigation key did, and what the page should draw because of it.</summary>
    public sealed class NavResult
    {
        /// <summary>The prompt text, when the terminal changed it. Null means leave the field alone.</summary>
        public string Line { get; internal set; }

        /// <summary>The suggestion block as page markup, or an empty string to hide it.</summary>
        public string Suggest { get; internal set; } = "";

        /// <summary>
        /// The grey completion that would sit behind the caret, or an empty string.
        ///
        /// Computed but not currently drawn. Placing it needs the typed text measured in the field's own font, and
        /// the field cannot be monospaced - a form control's text is written by the control with rich text off, so
        /// the fixed-advance tag never reaches it. Sideload would have to expose a text measurement for an app to
        /// put this in the right place, and until it does the highlighted row above the prompt shows the same thing
        /// a character further away.
        /// </summary>
        public string Ghost { get; internal set; } = "";
    }

    /// <summary>What running a line produced.</summary>
    public sealed class RunResult
    {
        public IReadOnlyList<OutputLine> Lines { get; internal set; } = Array.Empty<OutputLine>();

        /// <summary>Something to put on the system clipboard, or null. The host owns the clipboard.</summary>
        public string Clipboard { get; internal set; }

        /// <summary>The transcript was emptied - `clear`. The page has its own copy of the drawn window and would
        /// otherwise keep showing lines the terminal no longer has.</summary>
        public bool Cleared { get; internal set; }
    }

    /// <summary>
    /// The terminal itself: one line of input, one list of suggestions, one transcript.
    ///
    /// Everything the page can do arrives here as one of three calls - boot, nav, run - and the page keeps no state
    /// of its own beyond the text in the field. That is deliberate: the selected row, the history cursor and the
    /// reverse-search position all have to agree with each other, and the only way two copies of them cannot
    /// disagree is for there to be one copy.
    /// </summary>
    public sealed class Session
    {
        private readonly ICommandCatalogue _catalogue;
        private readonly ICommandRunner _runner;
        private readonly Suggestions _suggestions;
        private readonly Builtins _builtins;
        private readonly Transcript _transcript;
        private readonly History _history;
        private readonly Aliases _aliases;
        private readonly Usage _usage;

        private SuggestionSet _current = SuggestionSet.Empty;
        private int _selected;

        /// <summary>Where the history walk has got to, and what it started from. -1 means not walking; the draft is
        /// what the player had typed before they started, so walking back past the end restores it.</summary>
        private int _historyCursor = -1;
        private string _historyDraft = "";

        /// <summary>How far back Ctrl+R has looked, and for what.</summary>
        private int _searchSkip;
        private string _searchQuery = "";

        public Session(ICommandCatalogue catalogue, ICommandRunner runner, Usage usage,
                       History history, Aliases aliases)
        {
            _catalogue = catalogue;
            _runner = runner;
            _usage = usage;
            _history = history;
            _aliases = aliases;

            _transcript = new Transcript();
            _suggestions = new Suggestions(catalogue, usage, history, aliases);
            _builtins = new Builtins(_suggestions, catalogue, history, aliases, _transcript);
        }

        public Transcript Transcript => _transcript;

        public Builtins Builtins => _builtins;

        /// <summary>Whether the prompt accepts anything. False on a client, and false with the console switched
        /// off.</summary>
        public bool Locked => !_runner.CanRun;

        /// <summary>The banner the page shows on first open: what this terminal is, and why it might be locked.</summary>
        public IReadOnlyList<OutputLine> Banner()
        {
            var lines = new List<OutputLine>
            {
                OutputLine.Dim($"hash - {_catalogue.Commands.Count} commands. Tab completes, help lists, "
                               + "Up walks what you ran before."),
            };

            if (Locked)
            {
                lines.Add(OutputLine.Error(_runner.RefusalReason));
                lines.Add(OutputLine.Dim("Looking things up still works."));
            }

            return lines;
        }

        // ------------------------------------------------------------------------------------------- typing --

        /// <summary>The player typed. Recompute what is on offer and drop any walk they were in the middle of.</summary>
        public NavResult Typed(string line)
        {
            _historyCursor = -1;
            _searchSkip = 0;

            return Offer(line, resetSelection: true);
        }

        /// <summary>
        /// A navigation key. One entry point for all of them because they share the state they move through, and
        /// because the page should not have to know which keys mean "walk the list" and which mean "walk history".
        /// </summary>
        public NavResult Navigate(string line, string action)
        {
            switch ((action ?? "").ToLowerInvariant())
            {
                case "typed": return Typed(line);
                case "accept": return Accept(line);
                case "up": return Move(line, -1);
                case "down": return Move(line, +1);
                case "pageup": return Move(line, -Suggestions.MaxRows);
                case "pagedown": return Move(line, +Suggestions.MaxRows);
                case "search": return ReverseSearch(line);
                default: return Offer(line, resetSelection: false);
            }
        }

        /// <summary>
        /// Up and Down.
        ///
        /// With a list open they walk it, wrapping at both ends - a list you can only leave by pressing the other
        /// arrow the same number of times is a list you stop using. With no list open, Up walks history instead,
        /// which is what every shell does and what the vanilla console did before this mod took the key.
        /// </summary>
        private NavResult Move(string line, int by)
        {
            if (_current.Any)
            {
                int count = _current.Rows.Count;
                _selected = ((_selected + by) % count + count) % count;
                return Draw(line);
            }

            return WalkHistory(line, by);
        }

        private NavResult WalkHistory(string line, int by)
        {
            if (_history.Count == 0) return Draw(line);

            if (_historyCursor < 0)
            {
                if (by > 0) return Draw(line);   // Down with no walk in progress does nothing

                _historyDraft = line ?? "";
                _historyCursor = 0;
            }
            else
            {
                _historyCursor += by > 0 ? -1 : 1;
            }

            // Walked back past the newest entry: give the player their own half-typed line back.
            if (_historyCursor < 0)
            {
                _historyCursor = -1;
                return Offer(_historyDraft, resetSelection: true, forceLine: _historyDraft);
            }

            if (_historyCursor >= _history.Count) _historyCursor = _history.Count - 1;

            string recalled = _history.Lines[_historyCursor];
            return Offer(recalled, resetSelection: true, forceLine: recalled);
        }

        /// <summary>
        /// Ctrl+R: the most recent line containing what is typed, then the one before, and so on.
        ///
        /// The query is frozen at the first press. Re-reading the field each time would search for the line it just
        /// inserted, which finds itself and goes nowhere.
        /// </summary>
        private NavResult ReverseSearch(string line)
        {
            if (_searchSkip == 0) _searchQuery = (line ?? "").Trim();

            string hit = _history.ReverseSearch(_searchQuery, _searchSkip);
            if (hit == null)
            {
                // Nothing older. Say so rather than silently doing nothing, and let the next press start over.
                _searchSkip = 0;
                return Draw(line);
            }

            _searchSkip++;
            return Offer(hit, resetSelection: true, forceLine: hit);
        }

        /// <summary>
        /// Tab. Put the highlighted row into the line.
        ///
        /// A history row replaces the whole prompt, because it is a line and not a token. Everything else replaces
        /// the token under the caret, and a completed command word gets a trailing space - the next thing the player
        /// wants is the argument list, and making them press space for it is a keystroke that teaches nothing.
        /// </summary>
        private NavResult Accept(string line)
        {
            if (!_current.Any) return Draw(line);

            Suggestion pick = _current.Rows[Math.Min(_selected, _current.Rows.Count - 1)];

            if (pick.Kind == SuggestionKind.History)
                return Offer(pick.Value, resetSelection: true, forceLine: pick.Value);

            bool isCommandWord = pick.Kind == SuggestionKind.Command;
            string replaced = CommandLine.ReplaceTokenAtCaret(line, pick.Value, trailingSpace: isCommandWord);

            return Offer(replaced, resetSelection: true, forceLine: replaced);
        }

        // -------------------------------------------------------------------------------------------- running --

        /// <summary>
        /// Submit a line.
        ///
        /// The order is the whole contract: parse first, so a bad line costs nothing; echo before running, so the
        /// output has something to sit under; and remember it in history whether or not it worked, because the line
        /// you want to recall and fix is exactly the one that failed.
        /// </summary>
        public RunResult Run(string line)
        {
            var result = new RunResult();
            var lines = new List<OutputLine>();

            string typed = (line ?? "").Trim();
            if (typed.Length == 0) { result.Lines = lines; return result; }

            _current = SuggestionSet.Empty;
            _historyCursor = -1;
            _searchSkip = 0;

            Echo(typed, lines);
            _history.Add(typed);

            CommandLine.Plan plan = CommandLine.Parse(typed, _aliases.Expand);

            if (plan.Failed)
            {
                Emit(OutputLine.Error(plan.Error), lines);
                result.Lines = lines;
                return result;
            }

            int before = _transcript.Count;
            foreach (string command in plan.Commands) RunOne(command, lines);

            // The transcript shrinking across a run can only mean `clear`, which is the one thing the page cannot
            // work out for itself - it holds its own copy of the drawn window.
            result.Cleared = _transcript.Count < before;
            result.Lines = lines;
            result.Clipboard = _builtins.TakeClipboard();
            return result;
        }

        private void RunOne(string command, List<OutputLine> lines)
        {
            if (_builtins.TryRun(command, out IReadOnlyList<OutputLine> builtin))
            {
                foreach (OutputLine line in builtin) Emit(line, lines);
                return;
            }

            if (Locked)
            {
                Emit(OutputLine.Error(_runner.RefusalReason), lines);
                return;
            }

            _usage.Record(CommandLine.Tokenise(command));

            foreach (OutputLine line in _runner.Run(command)) Emit(line, lines);
        }

        /// <summary>Everything printed goes through here, so the transcript and what the page is handed can never
        /// drift apart - which is what `grep` and `copy` depend on.</summary>
        private void Emit(OutputLine line, List<OutputLine> into)
        {
            _transcript.Add(line);
            into.Add(line);
        }

        private void Echo(string typed, List<OutputLine> lines) => Emit(OutputLine.Echo("$ " + typed), lines);

        /// <summary>A line the host produced on its own - a captured log line while the log view is open.</summary>
        public void Push(OutputLine line) => _transcript.Add(line);

        // -------------------------------------------------------------------------------------------- drawing --

        private NavResult Offer(string line, bool resetSelection, string forceLine = null)
        {
            _current = _suggestions.For(line);
            if (resetSelection) _selected = 0;
            if (_selected >= _current.Rows.Count) _selected = 0;

            NavResult result = Draw(line);
            result.Line = forceLine;
            return result;
        }

        private NavResult Draw(string line) => new NavResult
        {
            Suggest = Markup.Suggestions(_current, _selected),
            Ghost = Ghost(line),
        };

        /// <summary>
        /// The grey remainder shown behind the caret.
        ///
        /// Only for a prefix match on the highlighted row: showing the tail of a fuzzy hit would put letters behind
        /// the caret that are not the ones about to be typed, which reads as the field having gone wrong.
        /// </summary>
        private string Ghost(string line)
        {
            if (!_current.Any) return "";

            Suggestion pick = _current.Rows[Math.Min(_selected, _current.Rows.Count - 1)];
            if (pick.Kind == SuggestionKind.History) return "";

            string prefix = _current.Prefix;
            if (prefix.Length == 0) return "";
            if (!pick.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";

            return pick.Value.Substring(prefix.Length);
        }

        /// <summary>The current block, for a redraw that changed nothing - used after a resize or a reload.</summary>
        public string SuggestMarkup() => Markup.Suggestions(_current, _selected);
    }
}
