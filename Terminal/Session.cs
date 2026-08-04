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
        /// The dimmed completion sitting behind the caret, or an empty string - what Tab would add, and nothing else.
        ///
        /// The page hands it to the renderer as <c>data-ghost</c> and it is drawn in the field's own font. It took a
        /// while to find a way that did not need measuring: the field cannot be monospaced, because a form control
        /// writes its text with rich text off and the fixed-advance tag never reaches it. The answer was to let TMP
        /// lay it out - the ghost element holds the typed text too, made invisible, so the visible part begins
        /// exactly where the typing stops.
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

        /// <summary>
        /// First row of the window that is actually drawn.
        ///
        /// The list keeps every match but only eight fit on the phone, so the window follows the selection instead
        /// of the list being cut to eight. Without it, an empty prompt shows the first eight of sixty-three commands
        /// and the arrows just wrap around them - you can never read the rest.
        /// </summary>
        private int _window;

        /// <summary>
        /// Whether the rows are drawn, as opposed to just the shape and what Tab would do.
        ///
        /// Closed while typing and opened by an arrow key. The list is eight lines tall and it takes them off the
        /// transcript, so leaving it open meant the output of the command you just ran was half hidden behind
        /// suggestions for the command you were typing next.
        /// </summary>
        private bool _expanded;

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

        /// <summary>
        /// How many words the prompt accepts.
        ///
        /// Counted the way the completion list is built rather than straight off the catalogue, so the header cannot
        /// claim a number the list then disagrees with: the terminal's own commands are in that list too, and a
        /// header saying 65 above a list of 75 is the kind of small lie that makes people distrust the rest.
        /// </summary>
        public int CommandCount
        {
            get
            {
                int count = _catalogue.Commands.Count;

                foreach (CommandInfo builtin in Builtins.Catalogue)
                    if (!_suggestions.IsCommand(builtin.Word)) count++;

                return count;
            }
        }

        /// <summary>
        /// What the terminal prints when it starts.
        ///
        /// Shaped like a shell's, because it is one: the first line names the program and what it is attached to, the
        /// second says how many commands are loaded and where to go next. Every shell worth using opens this way -
        /// cmd states its build, PowerShell its version, python tells you to type help. What it does NOT do is
        /// explain the keyboard: a line reading "Tab completes, Up walks what you ran before" is a tooltip, it says
        /// nothing about THIS session, and it is still sitting there on the twentieth open.
        ///
        /// <paramref name="identity"/> comes from the host because only the host can ask the game its version.
        /// </summary>
        public IReadOnlyList<OutputLine> Banner(string identity)
        {
            var lines = new List<OutputLine>();

            if (!string.IsNullOrEmpty(identity)) lines.Add(OutputLine.Out(identity));

            lines.Add(OutputLine.Dim($"{CommandCount} commands loaded. Type 'help' for the list."));

            if (Locked) lines.Add(OutputLine.Error(_runner.RefusalReason + " Lookups still work."));

            return lines;
        }

        // ------------------------------------------------------------------------------------------- typing --

        /// <summary>The player typed. Recompute what is on offer and drop any walk they were in the middle of.</summary>
        public NavResult Typed(string line)
        {
            _searchSkip = 0;

            // An open list STAYS open while the line is edited. Opening it is a decision - "show me what there is" -
            // and typing the next letter is the player narrowing that list, not withdrawing the question. Closing it
            // on every keystroke meant re-opening it after every keystroke.
            //
            // An empty line ends it, because there is nothing left to narrow, and so does submitting (see Run).
            if (string.IsNullOrWhiteSpace(line)) _expanded = false;

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
        /// They do exactly one thing: move the highlight through the list. Nothing reaches the prompt until Tab.
        /// A key that both moves and types is the worst of both - looking at the previous command means having it
        /// pasted in, and then deleted again before anything else can be typed.
        ///
        /// History is IN that list rather than a second mode reached through the same keys. Two modes on two keys
        /// is how Up ended up meaning "recall" from an empty prompt and "move" from a full one, and how the fourth
        /// press of Down followed by one Up jumped out of the list entirely.
        /// </summary>
        private NavResult Move(string line, int by)
        {
            // Nothing has worked out a list yet: the terminal was just opened, or the last thing that happened was a
            // submit. An arrow asks to see what there is, so compute it now - and stop there, because moving as well
            // would step straight past the row the player asked to look at.
            if (_current.Rows.Count == 0)
            {
                _current = _suggestions.For(line);
                _selected = 0;
                _window = 0;
            }

            if (!_current.Any) return Draw(line);

            // The first arrow OPENS the list and stops there - moving as well would step straight past the row the
            // player asked to look at. WHICH row depends on the arrow: Down opens at the top, on the commands, and
            // Up opens at the bottom, where history is and where the thing you just ran sits on the last line.
            if (!_expanded)
            {
                _expanded = true;
                _selected = by < 0 ? _current.Rows.Count - 1 : 0;

                ScrollToSelection();
                return Draw(line);
            }

            int count = _current.Rows.Count;
            _selected = ((_selected + by) % count + count) % count;

            ScrollToSelection();
            return Draw(line);
        }

        /// <summary>
        /// Put a line straight into the prompt with the list closed - what Ctrl+R does.
        ///
        /// Reverse search is the one place a key still types rather than highlights, because that is the whole
        /// gesture: keep pressing and keep replacing until the line you meant is there. A recalled line is also not
        /// a prefix anybody is completing, so offering to complete it would replace the list of everything with a
        /// list of one.
        /// </summary>
        private NavResult Recall(string line)
        {
            _current = SuggestionSet.Empty;
            _selected = 0;
            _window = 0;
            _expanded = false;

            return new NavResult { Line = line };
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
            return Recall(hit);
        }

        /// <summary>
        /// Tab. Put the highlighted row into the line.
        ///
        /// A history row replaces the whole prompt, because it is a line and not a token. Everything else replaces
        /// the token under the caret and is followed by a space when there is something left to type - which is
        /// every command word, and every argument the shape says is not the last one. Making the player press space
        /// between `give` and the item, or between the item and the quantity, is a keystroke that teaches nothing;
        /// adding one after the LAST argument is a keystroke they have to undo before Enter.
        /// </summary>
        private NavResult Accept(string line)
        {
            // Tab works with the list shut, which is its normal state - the row it takes is the one the header has
            // been showing all along.
            if (_current.Rows.Count == 0)
            {
                _current = _suggestions.For(line);
                _selected = 0;
                _window = 0;
            }

            if (!_current.Any) return Draw(line);

            Suggestion pick = _current.Rows[Math.Min(_selected, _current.Rows.Count - 1)];

            if (pick.Kind == SuggestionKind.History)
                return Offer(pick.Value, resetSelection: true, forceLine: pick.Value);

            bool more = pick.Kind == SuggestionKind.Command || MoreArgumentsAfter();
            string replaced = CommandLine.ReplaceTokenAtCaret(line, pick.Value, trailingSpace: more);

            return Offer(replaced, resetSelection: true, forceLine: replaced);
        }

        /// <summary>Whether the shape describes another argument after the one just completed. Unknown shapes say
        /// no: a space that turns out to be wrong is worse than one the player types themselves.</summary>
        private bool MoreArgumentsAfter()
        {
            if (_current.Command == null || _current.ArgIndex < 0) return false;

            return _current.ArgIndex + 1 < UsageExample.ArgumentCount(_current.Command.Signature);
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
            _searchSkip = 0;
            _expanded = false;

            // History expansion happens before anything else looks at the line, the way a shell does it - `!!` IS
            // the previous command by the time the parser sees it, so `repeat 3 !!` and `!! ; settime 1200` work
            // without either of them knowing that history exists.
            if (!Expand(ref typed, lines))
            {
                result.Lines = lines;
                return result;
            }

            Echo(typed, lines);
            _history.Add(typed);

            CommandLine.Plan plan = CommandLine.Parse(typed, _aliases.Expand);

            if (plan.Failed)
            {
                // A parse error is "what went wrong" and then "what it should look like", and the second of those is
                // a usage line - dim, and a line of its own so that grep, copy and the scroll window all count it as
                // one. Splitting here rather than at the source keeps the parser free of anything about drawing.
                string[] parts = plan.Error.Split('\n');

                Emit(OutputLine.Error(parts[0]), lines);
                for (int i = 1; i < parts.Length; i++) Emit(OutputLine.Dim(parts[i]), lines);

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

        /// <summary>
        /// Replace `!!` with the previous line and `!text` with the most recent line starting with it.
        ///
        /// Only when the line BEGINS with the mark, so an argument containing an exclamation mark is left alone.
        /// Returns false when nothing matched, having already said so - running `!zzz` as a command would produce
        /// "command not found" for something the player never typed.
        /// </summary>
        private bool Expand(ref string line, List<OutputLine> lines)
        {
            if (line.Length < 2 || line[0] != '!') return true;

            string rest = line.Substring(1);
            string found;

            if (rest == "!")
            {
                found = _history.Count > 0 ? _history.Lines[0] : null;
                if (found == null) { Emit(OutputLine.Warn("No previous command."), lines); return false; }
            }
            else
            {
                found = null;
                foreach (string past in _history.Lines)
                {
                    if (!past.StartsWith(rest, StringComparison.OrdinalIgnoreCase)) continue;

                    found = past;
                    break;
                }

                if (found == null) { Emit(OutputLine.Warn($"Nothing in history starts with '{rest}'."), lines); return false; }
            }

            line = found;
            return true;
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

            // A new set starts at the top. Carrying the old window over would open the list part-way down for no
            // reason the player could see.
            if (resetSelection) _window = 0;
            ScrollToSelection();

            NavResult result = Draw(line);
            result.Line = forceLine;
            return result;
        }

        /// <summary>
        /// Move the window the least amount that puts the selection back inside it.
        ///
        /// Least, rather than centring on the selection: a window that recentres on every keypress makes the whole
        /// list slide under the eye, and the row you were reading is never where you left it.
        /// </summary>
        private void ScrollToSelection()
        {
            int count = _current.Rows.Count;
            int visible = Suggestions.MaxRows;

            if (count <= visible) { _window = 0; return; }

            if (_selected < _window) _window = _selected;
            else if (_selected >= _window + visible) _window = _selected - visible + 1;

            if (_window > count - visible) _window = count - visible;
            if (_window < 0) _window = 0;
        }

        private NavResult Draw(string line) => new NavResult
        {
            Suggest = Markup.Suggestions(_current, _selected, _window, _expanded),
            Ghost = Ghost(line),
        };

        /// <summary>
        /// The dimmed remainder shown behind the caret - what Tab would add.
        ///
        /// Only for a PREFIX match on the highlighted row: the tail of a fuzzy hit would put letters behind the
        /// caret that are not the ones about to be typed, which reads as the field having gone wrong.
        ///
        /// An untouched prompt is the one case with nothing to say. Every command matches it, so the row that
        /// happens to be first is not a suggestion, it is the top of an alphabet - and pushing it into the field
        /// would make an empty prompt look occupied. Browsing changes that: a highlighted row IS a choice, and it
        /// belongs behind the caret whether or not anything was typed first.
        /// </summary>
        private string Ghost(string line)
        {
            if (!_current.Any) return "";

            Suggestion pick = _current.Rows[Math.Min(_selected, _current.Rows.Count - 1)];

            // A history row replaces the WHOLE line rather than the token under the caret, so its remainder is what
            // is left of the line - which is exactly the suggestion fish offers from history, and the reason it can
            // finish a command from three letters.
            if (pick.Kind == SuggestionKind.History)
            {
                string typed = line ?? "";
                if (typed.Length == 0) return _expanded ? pick.Value : "";

                return pick.Value.StartsWith(typed, StringComparison.OrdinalIgnoreCase)
                    ? pick.Value.Substring(typed.Length)
                    : "";
            }

            string prefix = _current.Prefix;
            if (prefix.Length == 0 && !_expanded) return "";
            if (!pick.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return "";

            return pick.Value.Substring(prefix.Length);
        }

        /// <summary>The current block, for a redraw that changed nothing - used after a resize or a reload.</summary>
        public string SuggestMarkup() => Markup.Suggestions(_current, _selected, _window, _expanded);
    }
}
