namespace Hash.Terminal
{
    /// <summary>
    /// Everything the shell needs from the running game, expressed so that none of it is here.
    ///
    /// This is the seam the whole project is built around: <c>Terminal/</c> compiles with no engine assembly, so the
    /// headless suite covers parsing, matching, ranking, history and rendering in a second instead of a game launch.
    /// Anything that has to ask the game - which commands exist, what an item is called, what a command printed -
    /// arrives through one of the interfaces below and is implemented in <c>Game/</c>.
    /// </summary>
    public interface ICommandCatalogue
    {
        /// <summary>Every console command the game currently knows, vanilla and modded, sorted by word.</summary>
        IReadOnlyList<CommandInfo> Commands { get; }

        /// <summary>
        /// Candidate values for one argument of one command, or an empty list when nothing can be offered.
        ///
        /// An empty list from a provider that OWNS the slot is different from having no provider at all: the first
        /// means "there are no vehicles", the second means "fall back to mining the usage example". The catalogue
        /// answers the first and <see cref="Owns"/> tells them apart.
        /// </summary>
        IReadOnlyList<ArgValue> ValuesFor(string command, int argIndex);

        /// <summary>Whether a provider claims this slot, empty or not.</summary>
        bool Owns(string command, int argIndex);
    }

    /// <summary>Running a line, and whether this player is allowed to.</summary>
    public interface ICommandRunner
    {
        /// <summary>False on a client, and false when the host turned the console off in the settings window.
        /// The prompt is locked either way.</summary>
        bool CanRun { get; }

        /// <summary>Why not, phrased for the player. Empty when <see cref="CanRun"/> is true.</summary>
        string RefusalReason { get; }

        /// <summary>
        /// Run one already-parsed command line and return what it printed.
        ///
        /// Output is captured rather than returned by the game: commands log through UnityEngine and the console has
        /// no output buffer of its own, so the implementation opens a window on the log around the call.
        /// </summary>
        IReadOnlyList<OutputLine> Run(string line);
    }

    /// <summary>
    /// Reading and writing the terminal's own files.
    ///
    /// Two scopes, because the two kinds of state answer different questions. Usage counts describe THIS save - they
    /// are how you played that world - so they live beside it. History and aliases describe the player, and starting
    /// a new save with an empty alias table feels like losing them.
    /// </summary>
    public interface IStore
    {
        /// <summary>Read a file, or null when it is not there. Never throws - a missing or corrupt file is a fresh
        /// start, not a crash on the first keystroke.</summary>
        string Read(StoreScope scope, string name);

        /// <summary>Write a file. Never throws.</summary>
        void Write(StoreScope scope, string name, string content);
    }

    public enum StoreScope
    {
        /// <summary>Beside the save. Gone when the save is deleted, which is correct for anything describing it.</summary>
        Save,

        /// <summary>Beside the mod, once per machine.</summary>
        Global,
    }

    /// <summary>A clock the tests can stand still. Only the log view needs one.</summary>
    public interface IClock
    {
        string Now { get; }
    }
}
