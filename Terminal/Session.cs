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

        /// <summary>A natural-language request is still running and the page should keep polling.</summary>
        public bool Pending { get; internal set; }

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
        private readonly ICommandCatalogue _naturalCatalogue;
        private readonly ICommandRunner _runner;
        private readonly Suggestions _suggestions;
        private readonly Builtins _builtins;
        private readonly Transcript _transcript;
        private readonly History _history;
        private readonly Aliases _aliases;
        private readonly Usage _usage;
        private readonly Marks _marks;
        private readonly MarkExpansion _expansion;
        private readonly INaturalCommandTranslator _natural;

        private NaturalCommandTranslation _naturalProposal;

        private const double NaturalAutoConfidence = 0.80;
        private const double NaturalConfirmConfidence = 0.50;

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
                       History history, Aliases aliases, IMarks marks = null,
                       INaturalCommandTranslator natural = null, ICommandCatalogue naturalCatalogue = null)
        {
            _catalogue = catalogue;
            _naturalCatalogue = naturalCatalogue ?? catalogue;
            _runner = runner;
            _usage = usage;
            _history = history;
            _aliases = aliases;
            _natural = natural;

            _marks = new Marks(marks);
            _expansion = new MarkExpansion(_marks, catalogue);

            _transcript = new Transcript();
            _suggestions = new Suggestions(catalogue, usage, history, aliases, _marks);
            _builtins = new Builtins(_suggestions, catalogue, history, aliases, _transcript);
        }

        public Transcript Transcript => _transcript;

        /// <summary>The context words - `#` and its family. The page shows what `#` currently points at.</summary>
        public Marks Marks => _marks;

        public Builtins Builtins => _builtins;

        /// <summary>Whether the prompt accepts anything. False on a client, and false with the console switched
        /// off.</summary>
        public bool Locked => !_runner.CanRun;

        /// <summary>Whether a background translation is still in flight.</summary>
        public bool NaturalPending => _natural?.Busy == true;

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

            // Beginning another line withdraws a pending request before its answer can race the player's typing.
            // Preserve a lone # because that is the explicit confirmation gesture for a medium-confidence proposal;
            // the first character after "# " makes it a new request and cancels the old one.
            string replacement = (line ?? "").Trim();
            if (replacement.Length > 0 && replacement != "#") CancelNatural();

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

            // A bare hash is deliberately handled before an ordinary line discards a waiting proposal. Everywhere
            // else `#` keeps its old meaning as an argument (`give # 1`); only the first whole token wakes Needle.
            if (typed == "#") return ConfirmNatural(typed, lines, result);

            // Any new submitted line supersedes an inference or proposal the player has left behind. A native call
            // cannot be stopped halfway through, but its generation is invalidated and its eventual answer ignored.
            CancelNatural();

            // History expansion happens before anything else looks at the line, the way a shell does it - `!!` IS
            // the previous command by the time the parser sees it, so `repeat 3 !!` and `!! ; settime 1200` work
            // without either of them knowing that history exists.
            if (!Expand(ref typed, lines))
            {
                result.Lines = lines;
                return result;
            }

            if (NaturalQuery(typed, out string query))
                return StartNatural(typed, query, lines, result);

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

            // Context words are replaced statement by statement, after parsing and before anything runs - so
            // `give # 1 ; teleport #` checks each half against the command it belongs to, and a refusal in the
            // second half stops the whole line rather than leaving the first half already done.
            var ready = new List<string>();
            foreach (string command in plan.Commands)
            {
                Expansion expanded = _expansion.Apply(command);
                if (expanded.Failed)
                {
                    foreach (string part in expanded.Error.Split('\n')) Emit(OutputLine.Error(part), lines);

                    result.Lines = lines;
                    return result;
                }

                ready.Add(expanded.Line);
            }

            _marks.Ran(typed);

            foreach (string command in ready) RunOne(command, lines);

            // The transcript shrinking across a run can only mean `clear`, which is the one thing the page cannot
            // work out for itself - it holds its own copy of the drawn window.
            result.Cleared = _transcript.Count < before;
            result.Lines = lines;
            result.Clipboard = _builtins.TakeClipboard();
            return result;
        }

        /// <summary>Take a completed background translation, applying the confidence policy on the game thread.</summary>
        public RunResult PollNatural()
        {
            var result = new RunResult { Pending = NaturalPending };
            var lines = new List<OutputLine>();

            if (_natural == null || !_natural.TryTake(out NaturalCommandTranslation translated))
            {
                result.Lines = lines;
                result.Pending = NaturalPending;
                return result;
            }

            result.Pending = NaturalPending;

            if (!string.IsNullOrEmpty(translated.Error))
            {
                Emit(OutputLine.Error("Hash: " + translated.Error), lines);
                _natural.Cancel();
            }
            else if (translated.Commands.Count == 0)
            {
                Emit(OutputLine.Warn("Hash: could not map that request to a console command."), lines);
                _natural.Cancel();
            }
            else if (translated.Confidence.HasValue && translated.Confidence.Value < NaturalConfirmConfidence)
            {
                string score = Confidence(translated.Confidence.Value);
                Emit(OutputLine.Warn("Hash: not confident enough (" + score + "). Please be more specific."), lines);
                _natural.Cancel();
            }
            else
            {
                ShowNatural(translated, lines);

            // Needle confidence is a ranking signal, not execution authorization. A live incident mapped `kill`
            // to `quit` at 97% and closed the game. Only a command Hash derived entirely from the current catalogue
            // may auto-run; every native prediction requires the user to confirm the concrete proposal with `#`.
            if (translated.Proven && translated.Confidence.HasValue
                                  && translated.Confidence.Value >= NaturalAutoConfidence)
                ExecuteNatural(translated, lines);
            else
            {
                    _naturalProposal = translated;
                    Emit(OutputLine.Warn("Type # to run this suggestion; any other command discards it."), lines);
                }
            }

            result.Lines = lines;
            result.Pending = NaturalPending;
            return result;
        }

        /// <summary>Invalidate a running translation or a proposal waiting for confirmation.</summary>
        public void CancelNatural()
        {
            bool active = _naturalProposal != null || NaturalPending;
            _naturalProposal = null;
            if (active) _natural?.Cancel();
        }

        private RunResult ConfirmNatural(string typed, List<OutputLine> lines, RunResult result)
        {
            Echo(typed, lines);
            _history.Add(typed);

            if (_naturalProposal != null)
            {
                NaturalCommandTranslation proposal = _naturalProposal;
                _naturalProposal = null;
                ExecuteNatural(proposal, lines);
            }
            else if (NaturalPending)
                Emit(OutputLine.Warn("Hash: still translating the previous request."), lines);
            else
                Emit(OutputLine.Dim("usage: # <request>"), lines);

            result.Lines = lines;
            result.Pending = NaturalPending;
            return result;
        }

        private RunResult StartNatural(string typed, string query, List<OutputLine> lines, RunResult result)
        {
            Echo(typed, lines);
            _history.Add(typed);

            if (Locked)
                Emit(OutputLine.Error(_runner.RefusalReason), lines);
            else if (_natural == null || !_natural.Available)
                Emit(OutputLine.Error(_natural?.UnavailableReason ?? "Hash is unavailable: Needle is not installed."), lines);
            else
            {
                try
                {
                    _natural.Start(query);
                    Emit(OutputLine.Dim("Hash: translating..."), lines);
                }
                catch (Exception e)
                {
                    Emit(OutputLine.Error("Hash: " + e.Message), lines);
                    _natural.Cancel();
                }
            }

            result.Lines = lines;
            result.Pending = NaturalPending;
            return result;
        }

        private void ShowNatural(NaturalCommandTranslation translated, List<OutputLine> lines)
        {
            string prefix = translated.Confidence.HasValue
                ? "Hash " + Confidence(translated.Confidence.Value) + ": "
                : "Hash: ";

            for (int i = 0; i < translated.Commands.Count; i++)
                Emit(OutputLine.Dim((i == 0 ? prefix : "        ") + translated.Commands[i]), lines);
        }

        private void ExecuteNatural(NaturalCommandTranslation translated, List<OutputLine> lines)
        {
            var ready = new List<string>();

            if (Locked)
            {
                Emit(OutputLine.Error(_runner.RefusalReason), lines);
                _natural?.Complete(new[] { new NaturalCommandExecution("", false, _runner.RefusalReason) });
                return;
            }

            // Validate the whole batch before the first side effect. The catalogue may have changed while the
            // worker was running, and mark expansion may reject a target that no longer exists.
            foreach (string command in translated.Commands)
            {
                List<string> tokens = CommandLine.Tokenise(command);
                string word = tokens.Count > 0 ? tokens[0] : "";
                CommandInfo current = _naturalCatalogue.Commands.FirstOrDefault(c =>
                    string.Equals(c.Word, word, StringComparison.OrdinalIgnoreCase));

                if (word.Length == 0 || word == "#" || current == null)
                {
                    string error = "Hash returned an unavailable command: " + (word.Length == 0 ? "(empty)" : word);
                    Emit(OutputLine.Error(error), lines);
                    _natural?.Complete(new[] { new NaturalCommandExecution(command, false, error) });
                    return;
                }

                if (!ValidateNaturalArguments(current, tokens, out string validationError))
                {
                    Emit(OutputLine.Error(validationError), lines);
                    _natural?.Complete(new[] { new NaturalCommandExecution(command, false, validationError) });
                    return;
                }

                Expansion expanded = _expansion.Apply(command);
                if (expanded.Failed)
                {
                    foreach (string part in expanded.Error.Split('\n')) Emit(OutputLine.Error(part), lines);
                    _natural?.Complete(new[] { new NaturalCommandExecution(command, false, expanded.Error) });
                    return;
                }

                ready.Add(expanded.Line);
            }

            var executions = new List<NaturalCommandExecution>();
            _marks.Ran(string.Join(" ; ", translated.Commands));

            for (int i = 0; i < ready.Count; i++)
            {
                int before = lines.Count;
                RunOne(ready[i], lines);
                IReadOnlyList<OutputLine> output = lines.Skip(before).ToList();
                bool success = !output.Any(line => line.Kind == LineKind.Error);
                executions.Add(new NaturalCommandExecution(translated.Commands[i], success,
                    string.Join("\n", output.Select(line => line.Text))));
            }

            _natural?.Complete(executions);
        }

        private bool ValidateNaturalArguments(CommandInfo command, IReadOnlyList<string> tokens, out string error)
        {
            error = null;
            List<string> shape = CommandLine.Tokenise(command.Signature);
            int supplied = Math.Max(0, tokens.Count - 1);
            int available = Math.Max(0, shape.Count - 1);
            int required = shape.Skip(1).Count(token => token.StartsWith("<", StringComparison.Ordinal));

            if (supplied < required || supplied > available)
            {
                error = "Hash returned the wrong number of arguments for " + command.Word
                        + ". Usage: " + command.Signature;
                return false;
            }

            for (int i = 0; i < supplied; i++)
            {
                string value = tokens[i + 1];
                if (Marks.IsWord(value)) continue; // MarkExpansion validates its existence and kind next.

                IReadOnlyList<ArgValue> current;
                bool owned;
                try
                {
                    current = _naturalCatalogue.ValuesFor(command.Word, i) ?? Array.Empty<ArgValue>();
                    owned = _naturalCatalogue.Owns(command.Word, i);
                }
                catch (Exception e)
                {
                    error = "Hash could not verify " + command.Word + " argument " + (i + 1) + ": " + e.Message;
                    return false;
                }

                if (current.Count == 0)
                {
                    if (!owned) continue;
                    error = "Hash returned a value for " + command.Word + " argument " + (i + 1)
                            + ", but no values are currently available.";
                    return false;
                }

                if (current.Any(candidate => string.Equals(candidate.Value, value, StringComparison.OrdinalIgnoreCase)))
                    continue;

                error = "Hash returned a value that is no longer available for " + command.Word
                        + " argument " + (i + 1) + ": " + value;
                return false;
            }

            return true;
        }

        private static bool NaturalQuery(string line, out string query)
        {
            query = "";
            if (string.IsNullOrEmpty(line) || line[0] != '#' || line.Length < 2)
                return false;

            query = line.Substring(1).Trim();
            return query.Length > 0;
        }

        private static string Confidence(double value) =>
            Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";

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

            // A one-word line the game printed is an id, and `#it` is how you use it without retyping it.
            if (line.Kind == LineKind.Out) _marks.Printed(line.Text);
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
