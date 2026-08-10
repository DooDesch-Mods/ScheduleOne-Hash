using Il2CppFishNet;
using Hash.Terminal;
using Il2CppScheduleOne.DevUtilities;
using GameConsole = Il2CppScheduleOne.Console;

namespace Hash.Game
{
    /// <summary>
    /// Handing a line to the game, and deciding whether this player may.
    ///
    /// The console is host-only three times over and the game says so exactly zero times: <c>IsConsoleEnabled</c>
    /// wants <c>IsServer</c>, <c>SetIsOpen</c> returns early for a non-host, and <c>SubmitCommand</c> itself checks
    /// <c>IsHost</c> and then does nothing at all - no warning, no line, nothing. A client presses the key and the
    /// game ignores them. The terminal says why instead, which is the entire difference.
    /// </summary>
    internal sealed class CommandRunner : ICommandRunner
    {
        private readonly LogCapture _log;

        internal CommandRunner(LogCapture log) => _log = log;

        /// <summary>
        /// Whether the log view is showing every line anyway.
        ///
        /// Set by the mod once the session exists. With the view on, the lines this held back are printed right
        /// underneath by the view itself, and a note saying they are hidden would be a lie.
        /// </summary>
        internal Func<bool> LogViewOpen { get; set; }

        public bool CanRun => Refusal() == null;

        public string RefusalReason => Refusal() ?? "";

        /// <summary>
        /// Why this player cannot run commands, or null.
        ///
        /// Checked live rather than cached: a player can host after joining a lobby, and the host can flip the
        /// console setting from the settings window mid-session.
        /// </summary>
        private static string Refusal()
        {
            try
            {
                // Singleplayer counts as host. A null NetworkManager means the world has not spawned yet, which is
                // not a refusal - it is the main menu, and the terminal there is a reference book.
                if (InstanceFinder.NetworkManager != null && !InstanceFinder.IsHost)
                    return "You are not the host. Commands run on the host's game only.";

                if (NetworkSingleton<Il2CppScheduleOne.DevUtilities.GameManager>.InstanceExists
                    && NetworkSingleton<Il2CppScheduleOne.DevUtilities.GameManager>.Instance?.Settings != null
                    && !NetworkSingleton<Il2CppScheduleOne.DevUtilities.GameManager>.Instance.Settings.ConsoleEnabled)
                    return "The console is switched off for this game. Turn it on in Settings > Gameplay.";
            }
            catch
            {
                // Anything unreadable here means the world is not up. Refusing on that would lock the terminal on
                // the main menu, where looking commands up is the whole point.
                return null;
            }

            return null;
        }

        /// <summary>
        /// Run one line and return what it printed.
        ///
        /// <c>SubmitCommand(string)</c> rather than the list overload, because the string one splits exactly the way
        /// the game means to split it - and because lower-casing every argument, which it also does, is what rescues
        /// a player who typed `give OGKush 5`.
        /// </summary>
        public IReadOnlyList<OutputLine> Run(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return Array.Empty<OutputLine>();

            _log.Open();

            try
            {
                // The terminal is on the phone, and taking the phone out unequips - see HeldSlot. Without this,
                // `packageproduct`, `setquality` and `setquantity` answer for an empty hand every single time.
                using (HeldSlot.Restored()) GameConsole.SubmitCommand(line);
            }
            catch (Exception e)
            {
                _log.Close();
                return new[] { OutputLine.Error("The command threw: " + e.Message) };
            }

            CapturedOutput captured = _log.Close();

            // A command that ran and said nothing is the normal case for half of them - `hideui` just hides the UI.
            // Silence after a successful run reads as a hang, so say something.
            if (captured.Lines.Count == 0 && captured.Hidden == 0) return new[] { OutputLine.Dim("ok") };

            var output = new List<OutputLine>(captured.Lines);

            // Named rather than dropped silently, because the one time it matters is when the answer looks wrong and
            // the reason is in the part that was left out.
            if (captured.Hidden > 0 && LogViewOpen?.Invoke() != true)
                output.Add(OutputLine.Dim(captured.Hidden == 1
                    ? "1 game log line hidden - 'logs' shows it"
                    : $"{captured.Hidden} game log lines hidden - 'logs' shows them"));

            return output;
        }
    }
}
