namespace Hash.Terminal
{
    /// <summary>What the terminal knows about one console command.</summary>
    public sealed class CommandInfo
    {
        public CommandInfo(string word, string description, string usage, string signature,
                           string source, bool isVanilla)
        {
            Word = word ?? "";
            Description = description ?? "";
            Usage = usage ?? "";
            Signature = signature ?? "";
            Source = source ?? "";
            IsVanilla = isVanilla;
        }

        /// <summary>The word typed to run it, lower-case.</summary>
        public string Word { get; }

        /// <summary>The game's own one-line description. Often empty for a mod's command.</summary>
        public string Description { get; }

        /// <summary>The game's own example, verbatim. Kept because it is the only place some commands say what
        /// their arguments look like.</summary>
        public string Usage { get; }

        /// <summary>The argument shape as the terminal shows it - <c>give &lt;item&gt; [quantity]</c>. Derived, not
        /// read: the game exposes no parameter names at all.</summary>
        public string Signature { get; }

        /// <summary>Who supplies it: "Vanilla", or a mod's name and version.</summary>
        public string Source { get; }

        public bool IsVanilla { get; }
    }

    /// <summary>One candidate value for one argument - an item id, a property code, an NPC.</summary>
    public readonly struct ArgValue
    {
        public ArgValue(string value, string source, bool isVanilla)
        {
            Value = value ?? "";
            Source = source ?? "";
            IsVanilla = isVanilla;
        }

        public string Value { get; }

        /// <summary>Which mod put this value in the game. Vanilla items say "Vanilla".</summary>
        public string Source { get; }

        public bool IsVanilla { get; }
    }

    /// <summary>How a transcript line is coloured, and what it means.</summary>
    public enum LineKind
    {
        /// <summary>The prompt and the line the player submitted, echoed back.</summary>
        Echo,

        /// <summary>Ordinary output.</summary>
        Out,

        /// <summary>A warning the command produced. Most vanilla failures are warnings, not errors.</summary>
        Warn,

        /// <summary>An error, or the terminal refusing something.</summary>
        Error,

        /// <summary>Framing the player did not ask for - a header, a hint, a separator.</summary>
        Dim,
    }

    /// <summary>One line of the transcript.</summary>
    public readonly struct OutputLine
    {
        public OutputLine(LineKind kind, string text)
        {
            Kind = kind;
            Text = text ?? "";
        }

        public LineKind Kind { get; }

        public string Text { get; }

        public static OutputLine Echo(string text) => new OutputLine(LineKind.Echo, text);

        public static OutputLine Out(string text) => new OutputLine(LineKind.Out, text);

        public static OutputLine Warn(string text) => new OutputLine(LineKind.Warn, text);

        public static OutputLine Error(string text) => new OutputLine(LineKind.Error, text);

        public static OutputLine Dim(string text) => new OutputLine(LineKind.Dim, text);
    }

    /// <summary>Where a suggestion came from, which decides how its row is drawn and what accepting it does.</summary>
    public enum SuggestionKind
    {
        /// <summary>A command word. Accepting it replaces the first token and adds a space.</summary>
        Command,

        /// <summary>A value for the argument under the caret. Accepting it replaces that token.</summary>
        Argument,

        /// <summary>A line the player ran before. Accepting it replaces the WHOLE prompt - it is not a token.</summary>
        History,
    }

    /// <summary>One row of the suggestion block.</summary>
    public readonly struct Suggestion
    {
        public Suggestion(SuggestionKind kind, string value, string source, bool isVanilla, int score)
        {
            Kind = kind;
            Value = value ?? "";
            Source = source ?? "";
            IsVanilla = isVanilla;
            Score = score;
        }

        public SuggestionKind Kind { get; }

        public string Value { get; }

        /// <summary>The label in the right-hand column: a mod name, "Vanilla", or "History".</summary>
        public string Source { get; }

        public bool IsVanilla { get; }

        /// <summary>How well the value matched what was typed. Used to keep usage ranking inside its band.</summary>
        public int Score { get; }
    }
}
