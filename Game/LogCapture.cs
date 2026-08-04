using Hash.Terminal;
using UnityEngine;

namespace Hash.Game
{
    /// <summary>
    /// What a command printed.
    ///
    /// The vanilla console has no output at all - <c>ConsoleUI</c> is one input field, and every command answers
    /// through <c>Console.Log</c>, which is three lines around <c>Debug.Log</c>. So the player runs `give bananas 1`
    /// and sees nothing happen, with the reason sitting in a log file behind the game.
    ///
    /// This is where that ends. One subscription to Unity's log callback, and a window opened around the call so
    /// every line logged in between belongs to the command that caused it. Commands run synchronously inside
    /// <c>SubmitCommand</c>, which is what makes the window exact rather than a guess.
    ///
    /// <para>Not a Harmony patch on <c>Console.Log</c>: those are three-line wrappers and prime inlining candidates
    /// under IL2CPP, so the patch would apply cleanly and never fire. The log callback is the sink vanilla's own
    /// <c>DebugLogCollector</c> uses, which is proof it works in this runtime.</para>
    /// </summary>
    internal sealed class LogCapture : IDisposable
    {
        /// <summary>Lines kept for the log view. A busy modded session fills this in a couple of minutes, and the
        /// view only ever shows the tail.</summary>
        private const int RingSize = 400;

        private readonly List<OutputLine> _ring = new();
        private readonly List<OutputLine> _window = new();

        private Application.LogCallback _callback;
        private bool _capturing;

        internal LogCapture()
        {
            // Converted rather than assigned, and kept in a field.
            //
            // Two IL2CPP facts meet here. The event takes an Il2Cpp delegate, so a managed method group cannot be
            // handed to it - DelegateSupport builds the bridge. And that bridge is a NEW object each time it is
            // built, so a second conversion would not equal the first and unsubscribing with it would silently
            // remove nothing.
            //
            // logMessageReceived rather than the Threaded variant: the capture window is opened and closed around a
            // synchronous call on the main thread, and a line arriving from a worker in between belongs to nobody.
            try
            {
                _callback = Il2CppInterop.Runtime.DelegateSupport
                    .ConvertDelegate<Application.LogCallback>((Action<string, string, LogType>)OnLog);

                Application.add_logMessageReceived(_callback);
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hash] could not listen to the game's log - commands will run but show nothing: " + e);
                _callback = null;
            }
        }

        public void Dispose()
        {
            if (_callback == null) return;

            try { Application.remove_logMessageReceived(_callback); }
            catch (Exception e) { Core.Log?.Warning("[hash] could not stop listening to the log: " + e.Message); }

            _callback = null;
        }

        /// <summary>Everything logged since the mod started, oldest first. What `logs` shows.</summary>
        internal IReadOnlyList<OutputLine> Ring => _ring;

        /// <summary>Start attributing lines to a command. Returns what it collects through <see cref="Close"/>.</summary>
        internal void Open()
        {
            _window.Clear();
            _capturing = true;
        }

        internal IReadOnlyList<OutputLine> Close()
        {
            _capturing = false;
            return new List<OutputLine>(_window);
        }

        private void OnLog(string message, string stackTrace, LogType type)
        {
            if (string.IsNullOrEmpty(message)) return;

            OutputLine line = new(Kind(type), Clean(message));

            Remember(line);
            if (_capturing) _window.Add(line);
        }

        private void Remember(OutputLine line)
        {
            _ring.Add(line);

            // Trimmed in blocks: removing the first element of a list copies everything after it, and a log storm
            // would do that once per line.
            if (_ring.Count > RingSize + 128) _ring.RemoveRange(0, _ring.Count - RingSize);
        }

        private static LineKind Kind(LogType type) => type switch
        {
            LogType.Warning => LineKind.Warn,
            LogType.Error or LogType.Exception or LogType.Assert => LineKind.Error,
            _ => LineKind.Out,
        };

        /// <summary>
        /// Trim what the terminal shows.
        ///
        /// MelonLoader prefixes a mod's lines with its own name in brackets, which is useful in a log file and noise
        /// in a terminal where the player already knows what they ran. Multi-line messages are flattened, because a
        /// transcript line is a line.
        /// </summary>
        private static string Clean(string message)
        {
            string one = message.Replace("\r", "").Replace('\n', ' ').Trim();

            return one.Length > 400 ? one.Substring(0, 397) + "..." : one;
        }
    }
}
