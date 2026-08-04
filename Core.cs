using Hash.Game;
using Hash.Terminal;
using MelonLoader;
using Sideload.Api;

[assembly: MelonInfo(typeof(Hash.Core), "hash", "1.0.0", "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Hash")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace Hash
{
    /// <summary>
    /// hash - a terminal on the in-game phone.
    ///
    /// Press the console key and the phone comes out with a terminal on it instead of the grey bar at the bottom of
    /// the screen. It completes commands and arguments, it shows what a command printed, and it remembers what you
    /// typed last session. None of that is in the game: the vanilla console is one input field with no output, no
    /// help and no memory.
    ///
    /// The mod is two halves that meet at the interfaces in Terminal/Ports.cs. <c>Terminal/</c> is the shell and
    /// knows nothing about Unity, which is why its behaviour is covered by a headless suite that runs in a second.
    /// <c>Game/</c> is everything that has to ask the running game. The page in <c>Assets/hash/</c> draws and
    /// forwards; it holds no state beyond the text in the field.
    /// </summary>
    public class Core : MelonMod
    {
        internal const string AppId = "hash";

        internal static MelonLogger.Instance Log;

        private static MelonPreferences_Entry<bool> _hijack;

        private AppHandle _app;
        private Session _session;
        private CommandIndex _index;
        private ArgProviders _providers;
        private LogCapture _log;
        private CommandRunner _runner;
        private Store _store;
        private History _history;
        private Aliases _aliases;
        private Usage _usage;
        private WorldMarks _marks;

        /// <summary>Whether the terminal was on screen last frame - used ONLY to notice when it leaves, so the
        /// session's history and aliases are written out. What the key does is decided by asking the game.</summary>
        private bool _wasOnScreen;

        private int _logsDrawnUpTo;

        /// <summary>Seconds between two checks of whether the console is switched on. See <see cref="IconFollowsTheConsole"/>.</summary>
        private const float IconInterval = 0.5f;

        private float _iconCheckedAt;

        /// <summary>The frame the terminal last answered the console key on. See <see cref="Toggle"/>.</summary>
        private int _toggledOnFrame = -1;

        public override void OnInitializeMelon()
        {
            Log = LoggerInstance;

            MelonPreferences_Category category = MelonPreferences.CreateCategory("Hash", "hash");
            _hijack = category.CreateEntry(
                "HijackConsoleKey", true, "Console key opens the terminal",
                "ON (default): the key that opened the console now takes the phone out with hash on it. OFF: the "
                + "vanilla console bar comes back and hash stays reachable only from code. Turn it off if another "
                + "mod needs the vanilla bar.");

            // Refuse rather than half-work. hash has no home-screen icon on purpose - the key is the only way in -
            // so a host that cannot raise the phone would leave the player with a mod that does nothing and no way
            // to find out why.
            if (!PhoneScreen.Available)
            {
                Log.Error("[hash] needs Sideload 1.5.0 or newer - this one cannot take the phone out of the "
                          + "player's pocket, so the terminal could never be reached. Nothing was registered.");
                return;
            }

            Build();
            RegisterApp();
            Patch();

            Log.Msg("[hash] ready. Press the console key.");
        }

        /// <summary>Wire the two halves together. Nothing here touches the game, so it is safe at init.</summary>
        private void Build()
        {
            _store = new Store();
            _log = new LogCapture();
            _providers = new ArgProviders();
            _index = new CommandIndex(_providers);

            _history = new History();
            _aliases = new Aliases();
            _usage = new Usage();

            _history.Load(_store);
            _aliases.Load(_store);

            _marks = new WorldMarks();
            _runner = new CommandRunner(_log);
            _session = new Session(_index, _runner, _usage, _history, _aliases, _marks);

            _runner.LogViewOpen = () => _session.Builtins.LogsOpen;
            _session.Builtins.UseFace(_store.Read(StoreScope.Global, "font"));
        }

        private void RegisterApp()
        {
            _app = Apps.Register(AppId, "Hash.Assets.hash", "hash", "hash")
                       .Orientation("landscape")
                       .NoIcon()
                       .OnCall("boot", _ => Boot())
                       .OnCall("nav", Nav)
                       .OnCall("run", Run)
                       .OnCall("drain", _ => Drain())
                       .OnCall("close", _ => { Toggle(); return ""; });
        }

        private void Patch()
        {
            ItemSourcePatch.Providers = _providers;
            // Declarations first, then the rebuild - the index is built from the list they were just added to.
            ConsoleAwakePatch.OnAwake = () =>
            {
                DeclaredCommands.Apply();
                _index.MarkDirty();
            };
            ConsoleKeyPatch.OnOpen = Toggle;
            ConsoleKeyPatch.Enabled = _hijack.Value;

            try
            {
                HarmonyInstance.PatchAll();
            }
            catch (Exception e)
            {
                Log.Error("[hash] patching failed - the console key will open the vanilla bar: " + e);
                ConsoleKeyPatch.Enabled = false;
            }
        }

        // ------------------------------------------------------------------------------------------ the key --

        /// <summary>
        /// The console key was pressed. Returns whether the terminal took it.
        ///
        /// Returning false hands the key back to the vanilla console, which is what should happen when the game
        /// refuses the phone - asleep, dead, arrested, paused. Silently swallowing the key in those states would
        /// look exactly like the mod having crashed.
        /// </summary>
        private bool Toggle()
        {
            if (_app == null) return false;

            // One press, one answer.
            //
            // The game has more than one ConsoleUI in the scene - Sideload sees the same duplication on the phone
            // side and says so ("skipping this second HomeScreen") - and each of them answers the key. Without this,
            // a single press ran the toggle twice in one frame: the first opened the terminal, the second read
            // _open as true and closed it again, so the phone flicked up and down and nothing appeared to happen.
            //
            // Still returns true for the repeat, so the vanilla console bar does not open behind it.
            int frame = UnityEngine.Time.frameCount;
            if (frame == _toggledOnFrame) return true;
            _toggledOnFrame = frame;

            // Asked, not remembered.
            //
            // A flag saying "the terminal is open" goes stale the moment anything else touches the phone, and
            // Escape does exactly that: it lowers the phone WITHOUT closing the app, so the app is still the phone's
            // current screen while nothing is on screen at all. The key then read the flag, decided the terminal was
            // up, and closed something the player could not see - so the phone only came back on the second press,
            // or not at all if the two answers kept passing each other.
            //
            // Both halves have to be true to count as visible, and both are read from the game.
            if (_app.IsOpen && PhoneScreen.IsRaised)
            {
                _app.Hide();
                _wasOnScreen = false;
                Persist();
                return true;
            }

            _index.MarkDirty();
            _providers.Invalidate();

            if (!_app.Show())
            {
                // The game refused the phone - asleep, dead, arrested, paused. Say so, because the alternative is a
                // key that silently does nothing and a player who thinks the mod is broken.
                Log.Warning("[hash] the game would not take the phone out right now, so the terminal stayed shut.");
                return false;
            }

            _wasOnScreen = true;

            // Tell the page it is on screen so it can put the caret back in the prompt.
            //
            // Needed because a reopen does not rebuild the page - the panel is simply shown again - so the startup
            // code that focused the field the first time never runs a second time. On the very first open there is
            // no page yet to hear this, which is fine: that is exactly the case the startup code covers.
            _app.Emit("shown", "");
            return true;
        }

        public override void OnUpdate()
        {
            // What # points at, sampled every frame - the game forgets its own hover the moment the phone comes up,
            // so there is nothing left to read at the point anyone would want to ask.
            _marks?.Tick();

            IconFollowsTheConsole();

            // The terminal can leave the screen without the key: the back gesture, another app, Escape, the player
            // putting the phone away. None of those are worth acting on except to write history and aliases out
            // while the session is still healthy - the key itself asks the game what is on screen, so nothing here
            // has to be kept in step with it.
            if (!_wasOnScreen || _app == null) return;

            if (_app.IsOpen && PhoneScreen.IsRaised) return;

            _wasOnScreen = false;
            Persist();
        }

        /// <summary>
        /// Put a square on the home screen exactly while the game would let commands run.
        ///
        /// A second way in for the player who never learns the key, and honest about the times there is nothing to
        /// open: the console setting is a live toggle in the settings window and the console is host-only, so the
        /// answer changes mid-session and the icon has to follow it.
        ///
        /// <para>Re-stated twice a second rather than remembered, because the phone is rebuilt on every scene load
        /// and a remembered "already showing" would be a promise about an icon that no longer exists. Two property
        /// reads and a lookup at 2 Hz costs nothing measurable.</para>
        /// </summary>
        private void IconFollowsTheConsole()
        {
            if (_app == null || _runner == null) return;

            float now = UnityEngine.Time.unscaledTime;
            if (now - _iconCheckedAt < IconInterval) return;

            _iconCheckedAt = now;
            _app.Icon(_runner.CanRun);
        }

        public override void OnDeinitializeMelon()
        {
            Persist();
            _log?.Dispose();
        }

        private void Persist()
        {
            _history.Save(_store);
            _aliases.Save(_store);
            _usage.Save(_store);

            // Per machine, not per save: which face is easier to read is a fact about the screen and the eyes in
            // front of it, not about the game being played.
            _store.Write(StoreScope.Global, "font", _session.Builtins.Face);
        }

        // --------------------------------------------------------------------------------- what the page asks --

        /// <summary>
        /// The page asking what to draw: who this player is, how many commands there are, and what is on screen.
        ///
        /// Called on every build, not only the first. Sideload throws a page away and builds it again for a hot
        /// reload, an orientation change and a reopen, so a boot that only ever returned the banner would wipe the
        /// transcript each time - the terminal would forget what it just told you because the phone turned.
        /// </summary>
        private string Boot()
        {
            _usage.Load(_store);
            _logsDrawnUpTo = _log.Ring.Count;

            if (_session.Transcript.Count == 0) _session.Transcript.Add(_session.Banner(Identity()));

            var json = new Json();
            json.Str("session", _session.Locked ? "session:client" : "session:host");
            json.Num("commands", _session.CommandCount);
            json.Str("prompt", "hash $");
            json.Str("version", Info?.Version ?? "");
            json.Str("mark", Mark());
            json.Bool("locked", _session.Locked);
            json.Bool("live", _session.Builtins.LogsOpen);
            json.Str("font", _session.Builtins.Face);
            json.Raw("banner", Lines(_session.Transcript.Window()));
            return json.Done();
        }

        /// <summary>
        /// The first line of the banner: this terminal and the game it is attached to.
        ///
        /// Both versions are read rather than written down. A banner that claims 1.0.0 after the mod has been
        /// updated, or names a game version the player is not running, is worse than no banner - it is the first
        /// thing anyone screenshots when reporting a bug.
        /// </summary>
        private string Identity()
        {
            string mine = Info?.Version ?? "";
            string game = "";

            try { game = UnityEngine.Application.version ?? ""; }
            catch { /* not worth a line in the log; the banner just says less */ }

            // 'v' before the number for the same reason the header carries one: in this font a bare 1 is a
            // vertical stroke and reads as a separator.
            return "hash" + (mine.Length > 0 ? " v" + mine : "")
                   + (game.Length > 0 ? " on Schedule I " + game : "");
        }

        /// <summary>A key that moves through what is on offer, or a keystroke that changed the line.</summary>
        private string Nav(string argument)
        {
            string line = Json.Field(argument, "line");
            string action = Json.Field(argument, "action");

            NavResult result = _session.Navigate(line, action);

            var json = new Json();
            if (result.Line != null) json.Str("line", result.Line);
            json.Str("suggest", result.Suggest);
            json.Str("mark", Mark());
            // Sent even though the page cannot place it yet - see NavResult.Ghost.
            json.Str("ghost", result.Ghost);
            return json.Done();
        }

        /// <summary>
        /// Whatever the game has logged since the page last asked, when the log view is on.
        ///
        /// Polled rather than pushed. Emitting on every captured line would rebuild the page several times a second
        /// while another mod chatters, and the player would be trying to type through it; asking once a second costs
        /// one rebuild and only while `logs` is actually open.
        /// </summary>
        private string Drain()
        {
            // A hidden page still has its timer. Answering with nothing keeps a terminal left open behind a closed
            // phone from rebuilding itself once a second for a screen nobody is looking at.
            bool wanted = _wasOnScreen && _session.Builtins.LogsOpen;

            var json = new Json();
            json.Raw("lines", Lines(wanted ? Fresh() : Array.Empty<OutputLine>()));
            json.Bool("live", _session.Builtins.LogsOpen);
            json.Str("font", _session.Builtins.Face);
            return json.Done();
        }

        /// <summary>
        /// What `#` points at right now, for the header.
        ///
        /// Sent with every answer rather than polled, because it changes while the player is not typing - they look
        /// somewhere else, put the phone away, come back. A header that lags behind is a header nobody trusts, and
        /// the string costs nothing next to what is already in the reply.
        /// </summary>
        private string Mark()
        {
            Hash.Terminal.Mark mark = _session.Marks.Resolve("#");

            return mark.Exists ? "# " + mark.Id : "";
        }

        /// <summary>A submitted line.</summary>
        private string Run(string line)
        {
            RunResult result = _session.Run(line);

            if (!string.IsNullOrEmpty(result.Clipboard)) Clipboard.Put(result.Clipboard);

            var json = new Json();
            json.Raw("lines", Lines(WithLogs(result.Lines)));
            json.Num("commands", _session.CommandCount);
            json.Bool("cleared", result.Cleared);
            json.Str("mark", Mark());
            json.Bool("live", _session.Builtins.LogsOpen);
            json.Str("font", _session.Builtins.Face);
            return json.Done();
        }

        /// <summary>
        /// Add whatever the game logged on its own since the last look, when the log view is on.
        ///
        /// Only drained on submit rather than streamed: a page that rebuilt every time another mod logged a line
        /// would rebuild several times a second while the player was trying to type.
        /// </summary>
        private IReadOnlyList<OutputLine> WithLogs(IReadOnlyList<OutputLine> ran)
        {
            if (!_session.Builtins.LogsOpen) { _logsDrawnUpTo = _log.Ring.Count; return ran; }

            var all = new List<OutputLine>(ran);
            all.AddRange(Fresh());
            return all;
        }

        /// <summary>Captured lines the page has not seen, filtered by whatever `logs` was given.</summary>
        private List<OutputLine> Fresh()
        {
            var fresh = new List<OutputLine>();
            string filter = _session.Builtins.LogsFilter;

            for (int i = Math.Max(0, _logsDrawnUpTo); i < _log.Ring.Count; i++)
            {
                OutputLine line = _log.Ring[i];

                if (filter.Length > 0 && line.Text.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && !filter.Equals(Kind(line), StringComparison.OrdinalIgnoreCase)) continue;

                _session.Push(line);
                fresh.Add(line);
            }

            _logsDrawnUpTo = _log.Ring.Count;
            return fresh;
        }

        private static string Kind(OutputLine line) => line.Kind switch
        {
            LineKind.Warn => "warn",
            LineKind.Error => "error",
            _ => "log",
        };

        /// <summary>The lines as a JSON array of {cls, text}, which is what the page draws.</summary>
        private static string Lines(IReadOnlyList<OutputLine> lines)
        {
            var sb = new System.Text.StringBuilder("[");

            for (int i = 0; i < lines.Count; i++)
            {
                if (i > 0) sb.Append(',');

                var one = new Json();
                one.Str("cls", Css(lines[i].Kind));
                one.Str("text", lines[i].Text);
                sb.Append(one.Done());
            }

            return sb.Append(']').ToString();
        }

        private static string Css(LineKind kind) => kind switch
        {
            LineKind.Echo => "echo",
            LineKind.Warn => "warn",
            LineKind.Error => "err",
            LineKind.Dim => "dim",
            _ => "",
        };
    }
}
