using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hash.Terminal;
using MelonLoader.Utils;

namespace Hash.Game
{
    /// <summary>
    /// The one background owner of Needle's process-global native engine. Catalogue snapshots are built by Start on
    /// the game thread; the worker only sees ordinary managed strings and never asks Unity or Schedule I anything.
    /// </summary>
    internal sealed class NeedleCommandTranslator : INaturalCommandTranslator, IDisposable
    {
        // Deliberately not .dll: MelonLoader scans managed DLLs in Mods, while NativeLibrary.Load accepts the
        // native PE under any filename. A distinct suffix keeps the engine out of the mod-assembly scan.
        internal const string LibraryFile = "Hash.Needle.bin";
        internal const string WeightsFile = "Hash.Needle.cact";

        private readonly ICommandCatalogue _catalogue;
        private readonly Marks _marks;
        private readonly Func<bool> _keepContext;
        private readonly string _libraryPath;
        private readonly string _weightsPath;
        private readonly bool _tuned;
        private readonly string _toolIndexPath;
        private readonly BlockingCollection<Work> _queue = new();
        private readonly Thread _worker;
        private readonly object _gate = new();

        private long _generation;
        private bool _busy;
        private bool _disposed;
        private NaturalCommandTranslation _ready;

#if DEBUG
        private long _diagnosticGeneration;
        private bool _diagnosticBusy;
        private NaturalCommandTranslation _diagnosticReady;
#endif

        internal NeedleCommandTranslator(ICommandCatalogue catalogue, Func<bool> keepContext,
                                         string libraryPath = null, string toolIndexPath = null,
                                         string weightsPath = null, IMarks marks = null)
        {
            _catalogue = catalogue;
            // Read when a request is prepared rather than held, so `#here` means where the player is now.
            _marks = marks == null ? null : new Marks(marks);
            _keepContext = keepContext ?? (() => false);
            _libraryPath = libraryPath ?? FindLibrary();
            _weightsPath = weightsPath ?? FindWeights();
            _tuned = File.Exists(_weightsPath);
            _toolIndexPath = toolIndexPath ?? Path.Combine(MelonEnvironment.UserDataDirectory, "Hash", "needle-tools.idx");

            // Which model answers is the difference between four requests in five and one in ten, and until this
            // line existed nothing said which one was loaded. Two measurements were taken against the fallback
            // before anyone noticed the file was missing.
            Core.Log?.Msg(_tuned
                ? "Needle is using the tuned model beside the mod (" + WeightsFile + ")."
                : "Needle is using its built-in base model: no " + WeightsFile + " beside Hash.dll. "
                  + "Requests will be routed noticeably worse - install the complete release package.");

            _worker = new Thread(WorkLoop)
            {
                IsBackground = true,
                Name = "Hash-Needle",
            };
            _worker.Start();
        }

        /// <summary>Which weights answered, for a shared record. "base" is the built-in fallback.</summary>
        internal string ModelName => _tuned ? WeightsFile : "base";

        public bool Available => File.Exists(_libraryPath);

        public string UnavailableReason => Available ? "" :
            "Hash is unavailable: the Needle engine " + LibraryFile + " must sit beside Hash.dll.";

        // A completed answer is still pending until the game thread consumes it. Reporting it here keeps a
        // newly submitted line from racing with PollNatural and accidentally executing a stale translation.
        public bool Busy { get { lock (_gate) return _busy || _ready != null; } }

        /// <summary>
        /// Queue the expensive native/model/tool-index initialization before the player submits anything. The
        /// catalogue is frozen here on the game thread; the background worker never reaches into Unity.
        /// </summary>
        internal bool Warmup()
        {
            if (_disposed || !Available) return false;

            NeedleToolset tools = NeedleToolset.Build(_catalogue, _marks).ForRouting();
            if (tools.Count == 0) return false;

            _queue.Add(Work.Warmup(tools));
            Core.Log?.Msg("Hash warmup queued for " + tools.Count + " tools.");
            return true;
        }

        public void Start(string query)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NeedleCommandTranslator));
            if (!Available) throw new FileNotFoundException(UnavailableReason, _libraryPath);

            NeedleToolset full = NeedleToolset.Build(_catalogue, _marks);
            if (full.Count == 0) throw new InvalidOperationException("there are no console commands to translate to");
            NeedleRequest prepared = full.Prepare(query);
            NeedleToolset tools = full.ForRouting();

            long generation;
            lock (_gate)
            {
                generation = ++_generation;
                _ready = prepared.Direct;
                _busy = prepared.Direct == null;
            }

            if (prepared.Direct != null)
            {
                Core.Log?.Msg("Hash resolved an explicit command locally.");
                return;
            }

            _queue.Add(Work.Query(generation, query, tools, _keepContext(), prepared));
        }

        public bool TryTake(out NaturalCommandTranslation translation)
        {
            lock (_gate)
            {
                translation = _ready;
                _ready = null;
                return translation != null;
            }
        }

#if DEBUG
        /// <summary>
        /// Run the real native translation path without publishing into Session. This exists only in debug builds
        /// so live regression tests can inspect Needle without ever executing its proposed console commands.
        /// </summary>
        internal void StartDiagnostic(string query)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NeedleCommandTranslator));
            if (!Available) throw new FileNotFoundException(UnavailableReason, _libraryPath);

            NeedleToolset tools = NeedleToolset.Build(_catalogue, _marks).ForRouting();
            if (tools.Count == 0) throw new InvalidOperationException("there are no console commands to translate to");
            NeedleRequest prepared = tools.Prepare(query);

            long generation;
            lock (_gate)
            {
                generation = ++_diagnosticGeneration;
                _diagnosticReady = prepared.Direct;
                _diagnosticBusy = prepared.Direct == null;
            }

            if (prepared.Direct == null)
                _queue.Add(Work.Diagnostic(generation, query, tools, prepared));
        }

        /// <summary>
        /// Diagnostic second/third pass used by the gold benchmark. It reruns the original evidence against only
        /// the command selected by the routing pass. Live provider values become grammar enums in this narrow schema;
        /// strict mode additionally requires optional slots. Results stay isolated from Session and can never run.
        /// </summary>
        internal void StartDiagnosticRefinement(string query, string commandWord, bool strict)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(NeedleCommandTranslator));
            if (!Available) throw new FileNotFoundException(UnavailableReason, _libraryPath);

            NeedleToolset routing = NeedleToolset.Build(_catalogue, _marks).ForRouting();
            if (!routing.TryConstrain(commandWord, query, strict, out NeedleToolset constrained, out string toolName))
                throw new InvalidOperationException("the selected command is not in the current catalogue: " + commandWord);

            var prepared = new NeedleRequest((query ?? "").Trim(), toolName, null, null);
            long generation;
            lock (_gate)
            {
                generation = ++_diagnosticGeneration;
                _diagnosticReady = null;
                _diagnosticBusy = true;
            }

            _queue.Add(Work.Diagnostic(generation, prepared.Text, constrained, prepared, routing));
        }

        internal bool DiagnosticBusy { get { lock (_gate) return _diagnosticBusy; } }

        internal bool TryTakeDiagnostic(out NaturalCommandTranslation translation)
        {
            lock (_gate)
            {
                translation = _diagnosticReady;
                _diagnosticReady = null;
                return translation != null;
            }
        }
#endif

        public void Complete(IReadOnlyList<NaturalCommandExecution> results)
        {
            if (_disposed) return;

            long generation;
            bool keep = _keepContext();
            lock (_gate)
            {
                generation = _generation;
                _busy = keep;
            }

            _queue.Add(Work.Feed(generation, ResultsJson(results), keep));
        }

        public void Cancel()
        {
            if (_disposed) return;

            long generation;
            lock (_gate)
            {
                generation = ++_generation;
                _ready = null;
                _busy = false;
            }

            // The reset is ordered behind an in-flight native call and ahead of any query Start enqueues next.
            _queue.Add(Work.Reset(generation));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            lock (_gate)
            {
                ++_generation;
                _ready = null;
                _busy = false;
#if DEBUG
                ++_diagnosticGeneration;
                _diagnosticReady = null;
                _diagnosticBusy = false;
#endif
            }

            _queue.CompleteAdding();
            _worker.Join(1500);
        }

        private void WorkLoop()
        {
            NativeNeedle native = null;
            string activeFingerprint = null;

            try
            {
                foreach (Work work in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        native ??= new NativeNeedle(_libraryPath, _tuned ? _weightsPath : null);

                        switch (work.Kind)
                        {
                            case WorkKind.Warmup:
                                Prepare(native, work.Tools, ref activeFingerprint);
                                native.Reset();
                                break;

                            case WorkKind.Query:
                                NaturalCommandTranslation result = Translate(
                                    native, work, ref activeFingerprint, out long inferenceMs, out bool constrained);
                                Core.Log?.Msg("Hash translated in " + inferenceMs + " ms"
                                              + (constrained ? " (refined against one command)." : "."));
#if DEBUG
                                Core.Log?.Msg("Hash Needle event " + JsonSerializer.Serialize(new
                                {
                                    type = "query",
                                    query = work.Text,
                                    commands = result.Commands,
                                    confidence = result.Confidence,
                                    error = result.Error,
                                    proven = result.Proven,
                                    constrained,
                                    elapsedMs = inferenceMs,
                                }));
#endif
                                Publish(work.Generation, constrained ? result.AsConstrained() : result, native);
                                if (constrained) Restore(native, work.Tools, ref activeFingerprint);
                                break;

#if DEBUG
                            case WorkKind.Diagnostic:
                                NaturalCommandTranslation diagnostic = Translate(
                                    native, work, ref activeFingerprint, out long diagnosticMs,
                                    out bool diagnosticConstrained);
                                Core.Log?.Msg("Hash diagnostic translated in " + diagnosticMs + " ms"
                                              + (diagnosticConstrained ? " (refined against one command)." : "."));
                                PublishDiagnostic(work.Generation, diagnostic, native);
                                if (work.RestoreTools != null)
                                    Restore(native, work.RestoreTools, ref activeFingerprint);
                                else if (diagnosticConstrained)
                                    Restore(native, work.Tools, ref activeFingerprint);
                                break;
#endif

                            case WorkKind.Feed:
                                if (work.KeepContext)
                                {
                                    string followup = native.Complete(work.Text);

                                    // Console translation is deliberately bounded to the calls from the original
                                    // request. Close an unexpected extra step with an error rather than executing it.
                                    if (HasCalls(followup))
                                        native.Complete("[{\"error\":\"additional calls are not supported\"}]");
                                }
                                else
                                    native.Reset();

                                Finish(work.Generation);
                                break;

                            case WorkKind.Reset:
                                native.Reset();
                                break;
                        }
                    }
                    catch (Exception e)
                    {
                        if (work.Kind == WorkKind.Query)
                            Publish(work.Generation,
                                new NaturalCommandTranslation(Array.Empty<string>(), null, Friendly(e)), native);
#if DEBUG
                        else if (work.Kind == WorkKind.Diagnostic)
                            PublishDiagnostic(work.Generation,
                                new NaturalCommandTranslation(Array.Empty<string>(), null, Friendly(e)), native);
#endif
                        else if (work.Kind == WorkKind.Warmup)
                        {
                            try { native?.Reset(); } catch { }
                            Core.Log?.Warning("Hash warmup failed: " + Friendly(e));
                        }
                        else
                        {
                            try { native?.Reset(); } catch { }
                            Finish(work.Generation);
                            Core.Log?.Warning("Hash follow-up failed: " + e.Message);
                        }
                    }
                }
            }
            finally
            {
                native?.Dispose();
            }
        }

        private NaturalCommandTranslation Translate(NativeNeedle native, Work work, ref string activeFingerprint,
                                                    out long elapsedMs, out bool constrained)
        {
            Prepare(native, work.Tools, ref activeFingerprint);
            if (!work.KeepContext) native.Reset();

            NeedleRequest request = work.Request ?? work.Tools.Prepare(work.Text);
            var inferenceClock = Stopwatch.StartNew();
            string response = native.Complete(request.Text);
            NaturalCommandTranslation result = work.Tools.ReadResponse(response, request.ExpectedToolName);

            if (work.Kind == WorkKind.Query && work.Tools.RouteOnly)
            {
                NeedleToolset refinement = null;
                string refinementName = null;

                // Empty, not null. NaturalCommandTranslation stores `error ?? ""`, so `Error == null` was false
                // for every answer the engine ever returned and this whole branch never ran: the second pass,
                // the one that fills in the arguments, was dead. A request that did not begin with a command
                // word came back as the bare command word, which the console then refused.
                if (string.IsNullOrEmpty(result.Error) && result.Commands.Count == 1)
                {
                    work.Tools.TryConstrain(result.Commands[0], work.Text, requireAll: false,
                                            out refinement, out refinementName);
                }
                else if (request.Fallback != null && (result.Commands.Count == 0 || IsCommandLockMiss(result)))
                {
                    refinement = request.Fallback;
                    refinementName = request.ExpectedToolName;
                }

                if (refinement != null)
                {
                    Prepare(native, refinement, ref activeFingerprint);
                    response = native.Complete(work.Text);
                    result = refinement.ReadResponse(response, refinementName);
                    constrained = true;
                    elapsedMs = inferenceClock.ElapsedMilliseconds;
                    return _tuned ? WithoutConfidence(result) : result;
                }

                if (string.IsNullOrEmpty(result.Error) && result.Commands.Count > 1)
                    result = new NaturalCommandTranslation(Array.Empty<string>(), result.Confidence,
                        "Hash selected more than one command route", reasoning: result.Reasoning,
                        prefillTps: result.PrefillTps, decodeTps: result.DecodeTps, peakRamMb: result.PeakRamMb);

                constrained = false;
                elapsedMs = inferenceClock.ElapsedMilliseconds;
                return _tuned ? WithoutConfidence(result) : result;
            }

            constrained = request.Fallback != null
                          && (result.Commands.Count == 0 || IsCommandLockMiss(result));
            if (constrained)
            {
                Core.Log?.Warning("Hash missed explicit command " + request.ExpectedToolName
                                  + " in broad retrieval; retrying it alone.");
                Prepare(native, request.Fallback, ref activeFingerprint);
                response = native.Complete(work.Text);
                result = request.Fallback.ReadResponse(response, request.ExpectedToolName);
            }

            elapsedMs = inferenceClock.ElapsedMilliseconds;
            return _tuned ? WithoutConfidence(result) : result;
        }

        private static NaturalCommandTranslation WithoutConfidence(NaturalCommandTranslation result) =>
            new(result.Commands, null, result.Error, result.Proven, result.Reasoning,
                result.PrefillTps, result.DecodeTps, result.PeakRamMb);

        private void Restore(NativeNeedle native, NeedleToolset tools, ref string activeFingerprint)
        {
            try
            {
                Prepare(native, tools, ref activeFingerprint);
                native.Reset();
            }
            catch (Exception e)
            {
                Core.Log?.Warning("Hash could not restore the warmed tool catalogue: " + Friendly(e));
            }
        }

        private void Publish(long generation, NaturalCommandTranslation result, NativeNeedle native)
        {
            bool current;
            lock (_gate)
            {
                current = generation == _generation && !_disposed;
                if (current)
                {
                    _ready = result;
                    _busy = false;
                }
            }

            if (!current)
            {
                try { native.Reset(); } catch { }
            }
        }

        private void Finish(long generation)
        {
            lock (_gate)
                if (generation == _generation) _busy = false;
        }

#if DEBUG
        private void PublishDiagnostic(long generation, NaturalCommandTranslation result, NativeNeedle native)
        {
            bool current;
            lock (_gate)
            {
                current = generation == _diagnosticGeneration && !_disposed;
                if (current)
                {
                    _diagnosticReady = result;
                    _diagnosticBusy = false;
                }
            }

            // Diagnostic conversations are deliberately never retained and cannot feed into a player session.
            try { native.Reset(); } catch { }
        }
#endif

        private void Prepare(NativeNeedle native, NeedleToolset tools, ref string activeFingerprint)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_toolIndexPath)!);

            if (string.Equals(activeFingerprint, tools.Fingerprint, StringComparison.Ordinal)) return;

            var initClock = Stopwatch.StartNew();
            native.Init(SystemFacts(), tools.Json, _toolIndexPath);
            activeFingerprint = tools.Fingerprint;
            Core.Log?.Msg("Hash prepared " + tools.Count + " tools in "
                          + initClock.ElapsedMilliseconds + " ms.");
        }

        private static bool IsCommandLockMiss(NaturalCommandTranslation result) =>
            result.Error.StartsWith("Hash ignored the explicit command ", StringComparison.Ordinal);

        private static string ResultsJson(IReadOnlyList<NaturalCommandExecution> results)
        {
            var payload = (results ?? Array.Empty<NaturalCommandExecution>()).Select(result => new Dictionary<string, object>
            {
                ["command"] = result.Command,
                ["success"] = result.Success,
                ["output"] = result.Output,
            });

            return JsonSerializer.Serialize(payload);
        }

        private static bool HasCalls(string response)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(response);
                return document.RootElement.TryGetProperty("function_calls", out JsonElement calls)
                       && calls.ValueKind == JsonValueKind.Array && calls.GetArrayLength() > 0;
            }
            catch { return false; }
        }

        private static string Friendly(Exception error) => error switch
        {
            BadImageFormatException => "the bundled Needle engine does not match this Windows architecture",
            EntryPointNotFoundException => "the bundled Needle engine has an incompatible API",
            DllNotFoundException => "the bundled Needle engine could not be loaded",
            _ => error.Message,
        };

        private static string SystemFacts() =>
            "locale: " + CultureInfo.CurrentUICulture.Name + "; device: phone; assistant: hash";

        private static string FindLibrary()
        {
            string besideAssembly = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                                                  LibraryFile);
            if (File.Exists(besideAssembly)) return besideAssembly;

            return Path.Combine(MelonEnvironment.ModsDirectory, LibraryFile);
        }

        private static string FindWeights()
        {
            string besideAssembly = Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "",
                                                  WeightsFile);
            if (File.Exists(besideAssembly)) return besideAssembly;

            return Path.Combine(MelonEnvironment.ModsDirectory, WeightsFile);
        }

        private enum WorkKind { Warmup, Query, Feed, Reset, Diagnostic }

        private sealed class Work
        {
            private Work(WorkKind kind, long generation, string text, NeedleToolset tools, bool keepContext,
                         NeedleRequest? request = null, NeedleToolset restoreTools = null)
            {
                Kind = kind;
                Generation = generation;
                Text = text;
                Tools = tools;
                KeepContext = keepContext;
                Request = request;
                RestoreTools = restoreTools;
            }

            internal WorkKind Kind { get; }
            internal long Generation { get; }
            internal string Text { get; }
            internal NeedleToolset Tools { get; }
            internal bool KeepContext { get; }
            internal NeedleRequest? Request { get; }
            internal NeedleToolset RestoreTools { get; }

            internal static Work Query(long generation, string text, NeedleToolset tools, bool keep,
                                       NeedleRequest request) =>
                new(WorkKind.Query, generation, text, tools, keep, request);

            internal static Work Warmup(NeedleToolset tools) =>
                new(WorkKind.Warmup, 0, null, tools, false);

#if DEBUG
            internal static Work Diagnostic(long generation, string text, NeedleToolset tools,
                                            NeedleRequest request, NeedleToolset restoreTools = null) =>
                new(WorkKind.Diagnostic, generation, text, tools, false, request, restoreTools);
#endif

            internal static Work Feed(long generation, string text, bool keep) =>
                new(WorkKind.Feed, generation, text, null, keep);

            internal static Work Reset(long generation) => new(WorkKind.Reset, generation, null, null, false);
        }
    }

    /// <summary>Small direct binding to Needle 2's stable C ABI.</summary>
    internal sealed class NativeNeedle : IDisposable
    {
        private const int BufferSize = 65536;
        private const int MaxNewTokens = 256;

        private readonly IntPtr _library;
        private readonly InitDelegate _init;
        private readonly CompleteDelegate _complete;
        private readonly ResetDelegate _reset;
        private readonly byte[] _weightsBlob;
        private GCHandle _weightsPin;

        internal NativeNeedle(string path, string weightsPath = null)
        {
            _library = NativeLibrary.Load(path);
            try
            {
                _init = Export<InitDelegate>("needle_init");
                _complete = Export<CompleteDelegate>("needle_complete");
                _reset = Export<ResetDelegate>("needle_reset");

                if (!string.IsNullOrEmpty(weightsPath))
                {
                    LoadDelegate load = Export<LoadDelegate>("needle_load");
                    _weightsBlob = File.ReadAllBytes(weightsPath);
                    if (_weightsBlob.Length == 0)
                        throw new InvalidOperationException("the tuned Needle weights file is empty: " + weightsPath);

                    _weightsPin = GCHandle.Alloc(_weightsBlob, GCHandleType.Pinned);
                    int rc = load(_weightsPin.AddrOfPinnedObject(), (ulong)_weightsBlob.LongLength);
                    if (rc != 0)
                        throw new InvalidOperationException("Needle rejected " + NeedleCommandTranslator.WeightsFile + " (code " + rc
                            + "). Tuned weights must be built for the bundled engine version.");
                }
            }
            catch
            {
                if (_weightsPin.IsAllocated) _weightsPin.Free();
                NativeLibrary.Free(_library);
                throw;
            }
        }

        internal void Init(string system, string tools, string indexPath)
        {
            using Utf8 systemUtf8 = new(system);
            using Utf8 toolsUtf8 = new(tools);
            using Utf8 indexUtf8 = new(indexPath);

            int rc = _init(systemUtf8.Pointer, toolsUtf8.Pointer, indexUtf8.Pointer);
            if (rc < 0) throw new InvalidOperationException("needle_init failed (code " + rc + ")");
        }

        internal string Complete(string text)
        {
            using Utf8 input = new(text);
            var output = new byte[BufferSize];
            GCHandle pinned = GCHandle.Alloc(output, GCHandleType.Pinned);

            try
            {
                int rc = _complete(input.Pointer, MaxNewTokens, pinned.AddrOfPinnedObject(), BufferSize);
                if (rc < 0) throw new InvalidOperationException("needle_complete failed (code " + rc + ")");
                // The return value is a status/token count, not the number of UTF-8 bytes written. The official
                // binding reads the NUL-terminated buffer as well; treating rc as a length truncates valid JSON.
                int length = Array.IndexOf(output, (byte)0);
                if (length < 0) throw new InvalidOperationException("needle_complete overflowed its response buffer");
                return System.Text.Encoding.UTF8.GetString(output, 0, length).TrimEnd('\0');
            }
            finally
            {
                pinned.Free();
            }
        }

        internal void Reset() => _reset();

        public void Dispose()
        {
            if (_library != IntPtr.Zero) NativeLibrary.Free(_library);
            if (_weightsPin.IsAllocated) _weightsPin.Free();
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int InitDelegate(IntPtr system, IntPtr tools, IntPtr toolIndexPath);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int CompleteDelegate(IntPtr text, int maxNewTokens, IntPtr responseBuffer, int bufferSize);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int LoadDelegate(IntPtr blob, ulong length);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate void ResetDelegate();

        private sealed class Utf8 : IDisposable
        {
            internal Utf8(string value) => Pointer = value == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);
            internal IntPtr Pointer { get; }
            public void Dispose() { if (Pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(Pointer); }
        }
    }
}
