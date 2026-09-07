using Hash.Game;
using Hash.Terminal;
using MelonLoader;
using Sideload.Api;

[assembly: MelonInfo(typeof(Hash.Core), "hash", DooDesch.ModVersion.Current, "DooDesch", "https://github.com/DooDesch-Mods/ScheduleOne-Hash")]
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
        private static MelonPreferences_Entry<bool> _needleKeepContext;
        private static MelonPreferences_Entry<bool> _needleShareUsage;

        private AppHandle _app;
        private Session _session;
        private UsageCapture _capture;
        private UsageReport _report;
        private CommandIndex _index;
        private ICommandCatalogue _naturalCatalogue;
        private ArgProviders _providers;
        private LogCapture _log;
        private CommandRunner _runner;
        private Store _store;
        private History _history;
        private Aliases _aliases;
        private Usage _usage;
        private WorldMarks _marks;
        private NeedleCommandTranslator _needle;

        /// <summary>One appearance, whether it began at the console key or at the live phone icon.</summary>
        private readonly ScreenPresence _screen = new();

        private int _logsDrawnUpTo;

        /// <summary>Seconds between two checks of whether the console is switched on. See <see cref="IconFollowsTheConsole"/>.</summary>
        private const float IconInterval = 0.5f;

        private float _iconCheckedAt;

        /// <summary>Warm Needle after the live console catalogue has had a moment to receive late mod commands.</summary>
        private const float NeedleWarmupDelay = 1f;

        private bool _needleWarmupPending;
        private float _needleWarmupAt;
        private int _needleWarmupFailures;

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
            _needleKeepContext = category.CreateEntry(
                "NeedleKeepContext", false, "Needle keeps conversation context",
                "OFF (default): every '# <request>' is translated independently. ON: later requests may refer to "
                + "earlier Needle commands and their results.");
            _needleShareUsage = category.CreateEntry(
                "NeedleShareUsage", false, "Share the '# ' request log",
                "OFF (default): nothing leaves this machine. hash always writes each '# <request>', the commands "
                + "it produced and whether they worked to UserData/Hash/queries.jsonl - open it and read it. ON: "
                + "that file is uploaded so the requests can be used to make the model better, and cleared once "
                + "the server has it. It holds no name, no save and no timestamp.");

            // Refuse rather than half-work. hash has no home-screen icon on purpose - the key is the only way in -
            // so a host that cannot raise the phone would leave the player with a mod that does nothing and no way
            // to find out why.
            if (!PhoneScreen.Available)
            {
                Log.Error("needs Sideload 1.5.0 or newer - this one cannot take the phone out of the "
                          + "player's pocket, so the terminal could never be reached. Nothing was registered.");
                return;
            }

            Build();
            RegisterApp();
            Patch();

            // Mods that declared a command word before this ran had nowhere to log it - see DeclaredCommands.Say.
            DeclaredCommands.FlushLog();

            Log.Msg("ready. Press the console key.");
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
            _naturalCatalogue = new OverlayCommandCatalogue(_index, Builtins.Catalogue);
            _needle = new NeedleCommandTranslator(_naturalCatalogue, () => _needleKeepContext.Value, marks: _marks);
            var sharing = new PreferenceSharing();
            _capture = new UsageCapture(_store, sharing, System.Globalization.CultureInfo.CurrentUICulture.Name)
            {
                // Which model answered decides what a record is evidence about: the fallback routes one
                // request in ten against the tuned model's four in five, and mod version cannot tell them
                // apart - the weights file can be missing at any version.
                Model = _needle.ModelName,
            };
            _report = new UsageReport(_store, sharing);
            _session = new Session(_index, _runner, _usage, _history, _aliases, _marks, _needle,
                                   _naturalCatalogue, _capture);

            _runner.LogViewOpen = () => _session.Builtins.LogsOpen;
            _session.Builtins.UseFace(_store.Read(StoreScope.Global, "font"));
        }

        /// <summary>
        /// The sharing answer, kept where every other setting is.
        ///
        /// Written from the terminal by `share on`, and readable and changeable in MelonPreferences.cfg by a
        /// player who never opens hash - a consent that can only be withdrawn from inside the thing collecting
        /// is not one.
        /// </summary>
        private sealed class PreferenceSharing : Hash.Terminal.IUsageSharing
        {
            public bool Enabled
            {
                get => _needleShareUsage?.Value == true;
                set
                {
                    // Set, do not save. MelonPreferences.Save() writes every mod's preferences and runs every
                    // mod's listeners: two test sessions died within 250 ms of `share on`, with another mod
                    // reacting to the save by re-syncing and broadcasting to all players from inside a callback
                    // on the game thread. The value takes effect immediately either way, and MelonLoader writes
                    // it out at shutdown like every other setting a player changes.
                    if (_needleShareUsage != null) _needleShareUsage.Value = value;
                }
            }
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
                       .OnCall("cancel", _ => Cancel())
                       .OnCall("back", _ => Back())
                       .OnCall("close", _ => { Toggle(); return ""; });

#if DEBUG
            // Read-only live regression hook. It snapshots the same catalogue/providers as a real request but
            // never enters Session or executes the resulting console line. The async pair additionally runs the
            // real native path, but publishes its answer only into an isolated diagnostic slot.
            _app.OnCall("needle-diagnose", DiagnoseNeedle)
                .OnCall("needle-diagnose-start", DiagnoseNeedleStart)
                .OnCall("needle-diagnose-refine-start", DiagnoseNeedleRefineStart)
                .OnCall("needle-diagnose-schema", _ => DiagnoseNeedleSchema())
                .OnCall("needle-diagnose-poll", _ => DiagnoseNeedlePoll());
#endif
        }

        private void Patch()
        {
            ItemSourcePatch.Providers = _providers;
            // Declarations first, then the rebuild - the index is built from the list they were just added to.
            ConsoleAwakePatch.OnAwake = () =>
            {
                DeclaredCommands.Apply();
                _index.MarkDirty();
                ScheduleNeedleWarmup();
            };
            ConsoleKeyPatch.OnOpen = Toggle;
            ConsoleKeyPatch.Enabled = _hijack.Value;

#if DEBUG
            // Before the rebuild below, for the same reason the vanilla declarations are: the index is built from
            // the list these are added to.
            Debugging.DevKit.Declare();
#endif

            try
            {
                HarmonyInstance.PatchAll();
            }
            catch (Exception e)
            {
                Log.Error("patching failed - the console key will open the vanilla bar: " + e);
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
                Left();
                return true;
            }

            // Read BEFORE Show, which is what raises it. This is the whole basis for where right-click leaves the
            // player: the key can be pressed with the phone already in their hand, and then it did not fetch it.
            bool raisedPhone = !PhoneScreen.IsRaised;

            if (!_app.Show())
            {
                // The game refused the phone - asleep, dead, arrested, paused. Say so, because the alternative is a
                // key that silently does nothing and a player who thinks the mod is broken.
                Log.Warning("the game would not take the phone out right now, so the terminal stayed shut.");
                return false;
            }

            Entered(raisedPhone);
            return true;
        }

        public override void OnUpdate()
        {
            WarmNeedle();

            // What # points at, sampled every frame - the game forgets its own hover the moment the phone comes up,
            // so there is nothing left to read at the point anyone would want to ask.
            _marks?.Tick();

            IconFollowsTheConsole();

            if (_app == null) return;

            bool onScreen = _app.IsOpen && PhoneScreen.IsRaised;
            if (onScreen)
            {
                // Toggle calls Entered synchronously for the console key. Reaching this transition with no recorded
                // appearance therefore means the player used the home-screen icon, which never passes Toggle.
                Entered(false);
            }
            else if (_screen.OnScreen)
                Left();
        }

        private void ScheduleNeedleWarmup()
        {
            _needleWarmupPending = true;
            _needleWarmupAt = UnityEngine.Time.unscaledTime + NeedleWarmupDelay;
            _needleWarmupFailures = 0;
        }

        private void WarmNeedle()
        {
            if (!_needleWarmupPending || _needle == null) return;

            float now = UnityEngine.Time.unscaledTime;
            if (now < _needleWarmupAt) return;

            if (!_needle.Available)
            {
                _needleWarmupPending = false;
                return;
            }

            try
            {
                // Capture the catalogue after the grace period, not at patch time: commands declared by another
                // mod during the same scene startup are then part of the warmed fingerprint as well.
                _providers.Invalidate();
                _index.MarkDirty();

                if (_needle.Warmup())
                {
                    _needleWarmupPending = false;
#if DEBUG
                    WriteNeedleSchemaSnapshot();
#endif
                }
                else
                    _needleWarmupAt = now + NeedleWarmupDelay;
            }
            catch (Exception e)
            {
                _needleWarmupFailures++;
                _needleWarmupPending = _needleWarmupFailures < 3;
                _needleWarmupAt = now + NeedleWarmupDelay;
                Log.Warning("Hash warmup could not be queued: " + e.Message);
            }
        }

        /// <summary>Start one visible appearance, from either of the app's two entry points.</summary>
        private void Entered(bool raisedPhone)
        {
            if (!_screen.Enter(raisedPhone)) return;

            _index.MarkDirty();
            _providers.Invalidate();

            // A reopen does not rebuild the page. This event restores the caret for both the key and icon paths;
            // on the very first build no listener exists yet, and the page's startup focus covers that case.
            _app?.Emit("shown", "");
        }

        /// <summary>
        /// The terminal is off the screen. EVERY way out has to end here - the key, the back press, another app, the
        /// player pocketing the phone - because the state left behind decides what the next appearance does, and a
        /// close path that only does half of this leaves the other half describing the session before it.
        /// </summary>
        private void Left()
        {
            if (!_screen.Leave()) return;

            _session?.CancelNatural();

            Persist();
        }

        /// <summary>
        /// Right-click inside the terminal. Leaves the way the player came in.
        ///
        /// The console key fetches the phone out of a pocket, so right-click puts it back - one press in, one press
        /// out, and the home screen in between is a place nobody was going. Pressing the icon happens on a phone that
        /// is already in their hand, and there the home screen IS the way back, so this hands the press to the host
        /// and lets it do what it does for every other app.
        ///
        /// <para>Answered rather than decided in the page: which of the two happened is a fact about the phone, and
        /// the page cannot see the phone at all.</para>
        /// </summary>
        private string Back()
        {
            // Read before the toggle: closing runs Left(), which clears the flag, so asking afterwards always
            // answers false and the page would let the host close the app a second time.
            bool handled = _screen.RaisedPhone;
            if (handled) Toggle();

            var json = new Json();
            json.Bool("handled", handled);
            return json.Done();
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
            _session?.CancelNatural();
            _needle?.Dispose();
            Persist();
            _log?.Dispose();
        }

        private void Persist()
        {
            // Before the rest: the open usage record is the only state here that a later line could still change,
            // and every path that reaches Persist is a path where no later line is coming. Sharing looks at the
            // finished file straight after, so a request made seconds ago is in the upload rather than the next one.
            _session?.CloseCapture();
            _report?.Pump();

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
            json.Bool("pending", _session.NaturalPending);
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
            json.Bool("pending", _session.NaturalPending);
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
            bool wanted = _screen.OnScreen && _session.Builtins.LogsOpen;
            RunResult natural = _screen.OnScreen ? _session.PollNatural() : new RunResult();
            var lines = new List<OutputLine>(natural.Lines);
            if (wanted) lines.AddRange(Fresh());

            var json = new Json();
            json.Raw("lines", Lines(lines));
            json.Bool("live", _session.Builtins.LogsOpen);
            json.Bool("pending", _session.NaturalPending);
            json.Str("font", _session.Builtins.Face);
            return json.Done();
        }

        private string Cancel()
        {
            _session.CancelNatural();

            var json = new Json();
            json.Bool("pending", false);
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
            json.Bool("pending", result.Pending);
            json.Str("font", _session.Builtins.Face);
            return json.Done();
        }

#if DEBUG
        private void WriteNeedleSchemaSnapshot()
        {
            try
            {
                string directory = System.IO.Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location) ?? AppContext.BaseDirectory;
                string path = System.IO.Path.Combine(directory, "Hash.Needle.Schema.json");
                System.IO.File.WriteAllText(path, DiagnoseNeedleSchema());
                Log.Msg("Hash wrote the Needle training schema snapshot to " + path + ".");
            }
            catch (Exception e)
            {
                Log.Warning("Hash could not write the Needle training schema snapshot: " + e.Message);
            }
        }

        private string DiagnoseNeedle(string query)
        {
            _providers.Invalidate();
            _index.MarkDirty();

            NeedleToolset tools = NeedleToolset.Build(_naturalCatalogue);
            NeedleRequest request = tools.Prepare((query ?? "").Trim());

            var json = new Json();
            json.Bool("ready", request.Direct != null);
            json.Num("tools", tools.Count);
            json.Str("fingerprint", tools.Fingerprint);
            json.Str("route", request.ExpectedToolName ?? "");
            json.Raw("commands", System.Text.Json.JsonSerializer.Serialize(
                request.Direct?.Commands ?? Array.Empty<string>()));
            return json.Done();
        }

        private string DiagnoseNeedleStart(string query)
        {
            var json = new Json();
            string text = (query ?? "").Trim();
            if (text.Length == 0)
                return json.Bool("started", false).Str("error", "query is empty").Done();

            try
            {
                _providers.Invalidate();
                _index.MarkDirty();
                _needle.StartDiagnostic(text);
                return json.Bool("started", true).Str("error", "").Done();
            }
            catch (Exception e)
            {
                return json.Bool("started", false).Str("error", e.Message).Done();
            }
        }

        private string DiagnoseNeedleSchema()
        {
            _providers.Invalidate();
            _index.MarkDirty();
            NeedleToolset tools = NeedleToolset.Build(_naturalCatalogue);
            var values = new Dictionary<string, List<List<string>>>(StringComparer.OrdinalIgnoreCase);

            foreach (CommandInfo command in _naturalCatalogue.Commands)
            {
                int slots = Math.Max(0, CommandLine.Tokenise(command.Signature).Count - 1);
                var commandValues = new List<List<string>>(slots);
                bool any = false;

                for (int slot = 0; slot < slots; slot++)
                {
                    List<string> current = (_naturalCatalogue.ValuesFor(command.Word, slot)
                        ?? Array.Empty<ArgValue>())
                        .Select(value => value.Value)
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                        .ToList();
                    commandValues.Add(current);
                    any |= current.Count > 0;
                }

                if (any) values[command.Word] = commandValues;
            }

            return new Json().Num("count", tools.Count).Str("fingerprint", tools.Fingerprint)
                .Raw("schemas", tools.Json)
                .Raw("commands", System.Text.Json.JsonSerializer.Serialize(
                    _naturalCatalogue.Commands.Select(command => new
                    {
                        name = command.Word,
                        description = command.Description,
                        usage = command.Usage,
                        signature = command.Signature,
                        source = command.Source,
                        vanilla = command.IsVanilla,
                        marks = Enumerable.Range(0, Math.Max(0,
                                CommandLine.Tokenise(command.Signature).Count - 1))
                            .Select(slot => _naturalCatalogue.KindOf(command.Word, slot)
                                .ToString().ToLowerInvariant())
                            .ToArray(),
                    })))
                .Raw("values", System.Text.Json.JsonSerializer.Serialize(values)).Done();
        }

        private string DiagnoseNeedleRefineStart(string payload)
        {
            var json = new Json();
            try
            {
                using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(payload ?? "");
                System.Text.Json.JsonElement root = document.RootElement;
                string query = root.TryGetProperty("query", out System.Text.Json.JsonElement queryValue)
                    ? queryValue.GetString()?.Trim() ?? "" : "";
                string command = root.TryGetProperty("command", out System.Text.Json.JsonElement commandValue)
                    ? commandValue.GetString()?.Trim() ?? "" : "";
                bool strict = root.TryGetProperty("strict", out System.Text.Json.JsonElement strictValue)
                              && strictValue.ValueKind == System.Text.Json.JsonValueKind.True;

                if (query.Length == 0 || command.Length == 0)
                    return json.Bool("started", false).Str("error", "query and command are required").Done();

                _providers.Invalidate();
                _index.MarkDirty();
                _needle.StartDiagnosticRefinement(query, command, strict);
                return json.Bool("started", true).Str("error", "").Done();
            }
            catch (Exception e)
            {
                return json.Bool("started", false).Str("error", e.Message).Done();
            }
        }

        private string DiagnoseNeedlePoll()
        {
            var json = new Json();
            if (!_needle.TryTakeDiagnostic(out NaturalCommandTranslation result))
                return json.Bool("ready", false).Bool("pending", _needle.DiagnosticBusy).Done();

            json.Bool("ready", true);
            json.Bool("pending", false);
            json.Raw("commands", System.Text.Json.JsonSerializer.Serialize(result.Commands));
            json.Raw("confidence", System.Text.Json.JsonSerializer.Serialize(result.Confidence));
            json.Str("error", result.Error);
            json.Str("reasoning", result.Reasoning);
            json.Raw("prefillTps", System.Text.Json.JsonSerializer.Serialize(result.PrefillTps));
            json.Raw("decodeTps", System.Text.Json.JsonSerializer.Serialize(result.DecodeTps));
            json.Raw("peakRamMb", System.Text.Json.JsonSerializer.Serialize(result.PeakRamMb));
            return json.Done();
        }
#endif

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
