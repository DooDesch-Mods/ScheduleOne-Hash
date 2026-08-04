using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// Turning what the player typed into commands to run.
    ///
    /// The shell has to earn its keep without ever getting in the way, so the rule is narrow and absolute: <b>a line
    /// with no shell syntax in it is passed through byte for byte.</b> `give ogkushseed 5` reaches the game exactly
    /// as it did before this mod existed. The shell only wakes up when the player writes something only a shell
    /// could mean - a quote, a semicolon, or one of the words in front.
    ///
    /// That leaves one hole, and <c>raw</c> is the plug: a command from another mod whose argument genuinely
    /// contains a semicolon or a quote would be taken apart by rules it never asked for. <c>raw &lt;line&gt;</c>
    /// hands the rest through untouched.
    /// </summary>
    public static class CommandLine
    {
        /// <summary>The word that turns the shell off for the rest of the line.</summary>
        public const string RawWord = "raw";

        /// <summary>The word that runs what follows several times.</summary>
        public const string RepeatWord = "repeat";

        /// <summary>Runaway guard. A hundred is more than any real use and small enough that a typo cannot hang the
        /// game: every repetition runs synchronously inside one frame.</summary>
        public const int MaxRepeat = 100;

        /// <summary>What one line of input turned into.</summary>
        public readonly struct Plan
        {
            internal Plan(IReadOnlyList<string> commands, string error)
            {
                Commands = commands ?? Array.Empty<string>();
                Error = error;
            }

            /// <summary>The command lines to run, in order, already expanded.</summary>
            public IReadOnlyList<string> Commands { get; }

            /// <summary>Set when the line could not be understood. Nothing runs in that case.</summary>
            public string Error { get; }

            public bool Failed => Error != null;
        }

        /// <summary>
        /// Read one line the player submitted.
        ///
        /// <paramref name="expandAlias"/> is asked for the first word of every command, after splitting and before
        /// anything else. Returning null means "not an alias". An alias is expanded once and not re-examined, which
        /// is what stops <c>alias give "give ogkush"</c> from recursing forever.
        /// </summary>
        public static Plan Parse(string line, Func<string, string> expandAlias = null)
        {
            if (string.IsNullOrWhiteSpace(line)) return new Plan(Array.Empty<string>(), null);

            string trimmed = line.Trim();

            // raw first, before anything looks at the rest. Whatever follows is the command, verbatim.
            if (StartsWithWord(trimmed, RawWord, out string rest))
            {
                return rest.Length == 0
                    ? new Plan(null, "raw: no command after it\nusage: raw <command>")
                    : new Plan(new[] { rest }, null);
            }

            // The fast path, and the promise. No quote and no semicolon means nothing here can change the line, so
            // it is not taken apart at all - it goes to the game the way it was typed.
            if (!NeedsShell(trimmed))
            {
                string expandedOnce = ExpandFirstWord(trimmed, expandAlias);
                return Split(expandedOnce, expandAlias, alreadyExpanded: !ReferenceEquals(expandedOnce, trimmed));
            }

            return Split(trimmed, expandAlias, alreadyExpanded: false);
        }

        /// <summary>
        /// Whether the line contains anything only a shell would read. Deliberately just two characters: the more
        /// this returns true, the more lines take a path they did not need.
        /// </summary>
        public static bool NeedsShell(string line) =>
            line != null && (line.IndexOf('"') >= 0 || line.IndexOf(';') >= 0);

        private static Plan Split(string line, Func<string, string> expandAlias, bool alreadyExpanded)
        {
            if (!TrySplitStatements(line, out List<string> statements, out string error))
                return new Plan(null, error);

            var plan = new List<string>();

            foreach (string statement in statements)
            {
                string one = statement.Trim();
                if (one.Length == 0) continue;

                if (StartsWithWord(one, RawWord, out string raw))
                {
                    if (raw.Length == 0) return new Plan(null, "raw: no command after it\nusage: raw <command>");
                    plan.Add(raw);
                    continue;
                }

                if (!alreadyExpanded) one = ExpandFirstWord(one, expandAlias);

                if (!Expand(one, plan, out error)) return new Plan(null, error);
            }

            return new Plan(plan, null);
        }

        /// <summary>Apply `repeat N`, then add the result. Split out because the repeated body may itself start with
        /// `raw`, and because the count has to be checked before anything runs.</summary>
        private static bool Expand(string statement, List<string> into, out string error)
        {
            error = null;

            if (!StartsWithWord(statement, RepeatWord, out string body))
            {
                into.Add(statement);
                return true;
            }

            int space = body.IndexOf(' ');
            string countText = space < 0 ? body : body.Substring(0, space);
            string command = space < 0 ? "" : body.Substring(space + 1).Trim();

            if (!int.TryParse(countText, out int count))
            {
                error = $"repeat: '{countText}' is not a number\nusage: repeat <count> <command>";
                return false;
            }

            if (count < 1)
            {
                error = "repeat: the count has to be 1 or more\nusage: repeat <count> <command>";
                return false;
            }

            if (count > MaxRepeat)
            {
                error = $"repeat: {MaxRepeat} is the most that can run in one frame";
                return false;
            }

            if (command.Length == 0)
            {
                error = "repeat: no command after the count\nusage: repeat <count> <command>";
                return false;
            }

            // The body keeps its own raw, so `repeat 3 raw weird;line` still works.
            if (StartsWithWord(command, RawWord, out string rawBody))
            {
                if (rawBody.Length == 0) { error = "raw: no command after it\nusage: raw <command>"; return false; }
                command = rawBody;
            }

            // The body is expanded in turn, so `repeat 5 repeat 5 give ogkushseed 1` is twenty-five gives. Without
            // this it was five copies of the line `repeat 5 give ogkushseed 1` handed to a game that has no `repeat`,
            // and the terminal had offered to complete the inner one all the way.
            //
            // Built into a list of its own first: the cap has to count what THIS repeat produced, and `into` already
            // holds whatever came before the semicolon.
            var made = new List<string>();

            for (int i = 0; i < count; i++)
            {
                if (!Expand(command, made, out error)) return false;

                if (made.Count > MaxRepeat)
                {
                    error = $"repeat: that comes to more than {MaxRepeat} commands, and they all run in one frame";
                    return false;
                }
            }

            into.AddRange(made);
            return true;
        }

        /// <summary>
        /// Break a line at top-level semicolons, leaving anything inside quotes alone.
        ///
        /// The quotes themselves are removed as they are consumed, so <c>settext "a; b"</c> reaches the game as
        /// <c>settext a; b</c> - one command with the semicolon intact - which is the entire reason quoting exists
        /// here.
        ///
        /// <para><b>raw eats the rest of the line.</b> Splitting has to notice it here rather than afterwards: by the
        /// time a statement exists, the semicolons that made it several statements are already gone. So at every
        /// point where a statement could start, the rest of the line is checked for <c>raw</c> - optionally behind a
        /// <c>repeat N</c>, because repeating a verbatim line is the one combination worth supporting - and if it is
        /// there, everything left becomes one statement.</para>
        /// </summary>
        private static bool TrySplitStatements(string line, out List<string> statements, out string error)
        {
            statements = new List<string>();
            error = null;

            var current = new StringBuilder();
            bool inQuotes = false;
            bool atStatementStart = true;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (atStatementStart && c != ' ')
                {
                    atStatementStart = false;

                    if (SuspendsSplitting(line.Substring(i)))
                    {
                        statements.Add(current.Append(line.Substring(i)).ToString());
                        return true;
                    }
                }

                if (c == '"')
                {
                    // A doubled quote inside a quoted run is one literal quote, the spreadsheet convention. There is
                    // no backslash escaping: a Windows path is a plausible argument and would be unusable.
                    if (inQuotes && i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                        continue;
                    }

                    inQuotes = !inQuotes;
                    continue;
                }

                if (c == ';' && !inQuotes)
                {
                    statements.Add(current.ToString());
                    current.Clear();
                    atStatementStart = true;
                    continue;
                }

                current.Append(c);
            }

            if (inQuotes)
            {
                error = "a quote was opened and never closed.";
                statements = null;
                return false;
            }

            statements.Add(current.ToString());
            return true;
        }

        /// <summary>
        /// Whether a statement starting here means "stop splitting": a bare <c>raw</c>, or a <c>repeat N raw</c>.
        ///
        /// The repeat form is here rather than left to the expander because the expander runs on a statement, and a
        /// statement no longer contains the semicolons that this decision is about.
        /// </summary>
        private static bool SuspendsSplitting(string rest)
        {
            if (StartsWithWord(rest, RawWord, out _)) return true;
            if (!StartsWithWord(rest, RepeatWord, out string afterRepeat)) return false;

            int space = afterRepeat.IndexOf(' ');
            if (space <= 0) return false;
            if (!int.TryParse(afterRepeat.Substring(0, space), out _)) return false;

            return StartsWithWord(afterRepeat.Substring(space + 1).TrimStart(), RawWord, out _);
        }

        /// <summary>Replace the first word if it is an alias. Returns the same instance when nothing changed, which
        /// is how the caller knows not to expand a second time.</summary>
        private static string ExpandFirstWord(string statement, Func<string, string> expandAlias)
        {
            if (expandAlias == null) return statement;

            int space = statement.IndexOf(' ');
            string word = space < 0 ? statement : statement.Substring(0, space);

            string replacement = expandAlias(word);
            if (string.IsNullOrEmpty(replacement)) return statement;

            return space < 0 ? replacement : replacement + statement.Substring(space);
        }

        /// <summary>
        /// Peel off the words that take a command as their argument, so what is left completes as the command it
        /// actually is.
        ///
        /// `repeat 5 give og` runs `give og` five times, so that is what the caret is inside and that is what should
        /// be offered - the alternative is a terminal where wrapping a command in `repeat` costs you every
        /// suggestion, which teaches people not to use it.
        ///
        /// A wrapper's OWN arguments are peeled only once they are finished, and "finished" means a space after
        /// them: `repeat 5` is someone still typing the count, `repeat 5 ` is someone starting the command.
        /// </summary>
        public static string Unwrap(string statement)
        {
            if (string.IsNullOrEmpty(statement)) return statement ?? "";

            // Bounded rather than while(true): `raw raw raw` is legal, silly, and must not spin.
            for (int guard = 0; guard < 4; guard++)
            {
                string rest = statement.TrimStart();
                int peel = WrapperWidth(rest);
                if (peel <= 0) return statement;

                statement = rest.Substring(peel);
            }

            return statement;
        }

        /// <summary>
        /// Words whose LAST argument is itself a command line, and how many arguments of their own come first.
        ///
        /// Two of these change what runs - `raw` suspends the shell, `repeat` multiplies it - and two do not:
        /// `alias gk "give ogkush 5"` and `bind t 'settime 1200'` merely store a line for later. Completion does not
        /// care about the difference. All four end in a command, so all four should complete like one, and keeping
        /// them in one table is what stops the next word of this kind from being forgotten.
        /// </summary>
        private static readonly (string Word, int OwnArguments, bool RunsNow)[] TakeACommand =
        {
            (RawWord, 0, true),
            (RepeatWord, 1, true),
            ("alias", 1, false),
            ("bind", 1, false),
        };

        /// <summary>
        /// Where a STORED command line starts in this statement, or -1.
        ///
        /// The difference between `repeat 3 teleport #` and `alias t "teleport #"` is when the command runs. The
        /// first runs now, so `#` means what is marked now. The second is being written down for later, so `#` has
        /// to survive as the character `#` and be resolved every time the alias is used - an alias that froze the
        /// mark at the moment it was defined would be a different command every session and the same command
        /// forever after.
        /// </summary>
        public static int StoredCommandAt(string statement)
        {
            if (string.IsNullOrEmpty(statement)) return -1;

            string trimmed = statement.TrimStart();
            int at = 0;
            string word = ReadWord(trimmed, ref at);

            foreach ((string candidate, int arguments, bool runsNow) in TakeACommand)
            {
                if (runsNow) continue;
                if (!string.Equals(word, candidate, StringComparison.OrdinalIgnoreCase)) continue;

                // Its own arguments come first; the stored line starts at the token after them.
                return arguments + 1;
            }

            return -1;
        }

        /// <summary>How many characters at the front are the word plus its own arguments plus the space after them,
        /// or 0 when this word takes no command or the player is still typing its own arguments.</summary>
        private static int WrapperWidth(string statement)
        {
            int at = 0;
            string word = ReadWord(statement, ref at);

            int ownArguments = -1;
            foreach ((string candidate, int arguments, bool _) in TakeACommand)
                if (string.Equals(word, candidate, StringComparison.OrdinalIgnoreCase)) ownArguments = arguments;

            if (ownArguments < 0) return 0;

            for (int i = 0; i <= ownArguments; i++)
            {
                if (at >= statement.Length || statement[at] != ' ') return 0;
                while (at < statement.Length && statement[at] == ' ') at++;

                if (i < ownArguments && ReadWord(statement, ref at).Length == 0) return 0;
            }

            // An embedded command is usually quoted, because it has spaces in it - `alias gk "give ogkush 5"`. The
            // quote is not part of the command, and leaving it on would make the first word `"give` and match
            // nothing at all.
            if (at < statement.Length && (statement[at] == '"' || statement[at] == '\'')) at++;

            return at;
        }

        private static string ReadWord(string line, ref int at)
        {
            int start = at;
            while (at < line.Length && line[at] != ' ') at++;

            return line.Substring(start, at - start);
        }

        /// <summary>True when the line begins with this word followed by a space or nothing - not merely with those
        /// letters, so `rawhide` is a command and not a `raw` with a typo.</summary>
        public static bool StartsWithWord(string line, string word, out string rest)
        {
            rest = null;
            if (line == null || word == null) return false;
            if (!line.StartsWith(word, StringComparison.OrdinalIgnoreCase)) return false;

            if (line.Length == word.Length) { rest = ""; return true; }
            if (line[word.Length] != ' ') return false;

            rest = line.Substring(word.Length + 1).Trim();
            return true;
        }

        /// <summary>
        /// Split one command into its words the way the game does - on spaces, dropping empty runs - but honouring
        /// quotes so a value with a space in it survives.
        ///
        /// Used for reading a line, never for running one: the game gets the string and splits it itself.
        /// </summary>
        public static List<string> Tokenise(string command)
        {
            var tokens = new List<string>();
            if (string.IsNullOrEmpty(command)) return tokens;

            var current = new StringBuilder();
            bool inQuotes = false;
            bool any = false;

            foreach (char c in command)
            {
                if (c == '"') { inQuotes = !inQuotes; any = true; continue; }

                if (c == ' ' && !inQuotes)
                {
                    if (any) { tokens.Add(current.ToString()); current.Clear(); any = false; }
                    continue;
                }

                current.Append(c);
                any = true;
            }

            if (any) tokens.Add(current.ToString());
            return tokens;
        }

        /// <summary>
        /// Which token the caret sits in, and what has been typed of it - the two things every completion needs.
        ///
        /// A caret directly after a space is in a NEW, empty token: the player finished a word and wants the next
        /// one offered. That is the difference between `give ` listing every item and listing nothing.
        /// </summary>
        public static void TokenAtCaret(string line, out int index, out string prefix)
        {
            index = 0;
            prefix = "";
            if (string.IsNullOrEmpty(line)) return;

            bool inQuotes = false;
            var current = new StringBuilder();
            bool started = false;

            foreach (char c in line)
            {
                if (c == '"') { inQuotes = !inQuotes; started = true; continue; }

                if (c == ' ' && !inQuotes)
                {
                    if (started) { index++; current.Clear(); started = false; }
                    continue;
                }

                current.Append(c);
                started = true;
            }

            prefix = current.ToString();
        }

        /// <summary>
        /// Put a value into the token the caret is in, and give back the whole line.
        ///
        /// Everything before the token is kept byte for byte, including the spacing the player typed. A value with a
        /// space in it comes back quoted, because that is the only way it survives the round trip.
        /// </summary>
        public static string ReplaceTokenAtCaret(string line, string value, bool trailingSpace)
        {
            string quoted = value != null && value.IndexOf(' ') >= 0 ? "\"" + value + "\"" : value ?? "";

            int cut = line?.Length ?? 0;
            if (line != null)
            {
                bool inQuotes = false;
                for (int i = line.Length - 1; i >= 0; i--)
                {
                    if (line[i] == '"') inQuotes = !inQuotes;
                    if (line[i] == ' ' && !inQuotes) { cut = i + 1; break; }
                    if (i == 0) cut = 0;
                }
            }

            string head = line == null ? "" : line.Substring(0, cut);
            return head + quoted + (trailingSpace ? " " : "");
        }
    }
}
