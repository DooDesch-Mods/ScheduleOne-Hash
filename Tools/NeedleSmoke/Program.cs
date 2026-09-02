using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length > 0 && string.Equals(args[0], "benchmark", StringComparison.OrdinalIgnoreCase))
    return RunBenchmark(args.Skip(1).ToArray());

if (args.Length is < 1 or > 3 || !File.Exists(args[0]) || (args.Length == 3 && !File.Exists(args[2])))
{
    Console.Error.WriteLine("usage: NeedleSmoke <path-to-Hash.Needle.bin> [query] [tools-json-file]");
    return 2;
}

const string defaultTools = """
[
  {
    "name": "set_time",
    "description": "Set the in-game time",
    "parameters": {
      "type": "object",
      "properties": {
        "time": { "type": "integer", "description": "time in 24-hour HHMM form" }
      },
      "required": ["time"]
    }
  }
]
""";
string tools = args.Length == 3 ? File.ReadAllText(args[2]) : defaultTools;

using var needle = new Needle(args[0]);
needle.Init("locale: en-US; device: phone; assistant: hash", tools);
string response = needle.Complete(args.Length == 2 ? args[1] : "set the time to 1200");

using JsonDocument json = JsonDocument.Parse(response);
JsonElement root = json.RootElement;
if (!root.TryGetProperty("success", out JsonElement success) || success.ValueKind != JsonValueKind.True)
    throw new InvalidOperationException("Needle did not report success: " + response);
if (!root.TryGetProperty("function_calls", out JsonElement calls) ||
    calls.ValueKind != JsonValueKind.Array || calls.GetArrayLength() == 0)
    throw new InvalidOperationException("Needle returned no function call: " + response);
string? selected = calls[0].GetProperty("name").GetString();
using JsonDocument declared = JsonDocument.Parse(tools);
if (!declared.RootElement.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == selected))
    throw new InvalidOperationException("Needle chose an undeclared tool: " + response);

Console.WriteLine(response);
return 0;

static int RunBenchmark(string[] args)
{
    if (args.Length != 4 || !File.Exists(args[0]) || !File.Exists(args[1]) || !File.Exists(args[2]))
    {
        Console.Error.WriteLine("usage: NeedleSmoke benchmark <engine> <weights.cact> <dataset.jsonl> <report.json>");
        return 2;
    }

    string enginePath = Path.GetFullPath(args[0]);
    string weightsPath = Path.GetFullPath(args[1]);
    string datasetPath = Path.GetFullPath(args[2]);
    string reportPath = Path.GetFullPath(args[3]);
    var results = new List<BenchmarkResult>();
    var totalClock = Stopwatch.StartNew();

    using (var needle = new Needle(enginePath, weightsPath))
    {
        foreach (string line in File.ReadLines(datasetPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonNode row = JsonNode.Parse(line) ?? throw new InvalidDataException("dataset row is not JSON");
            string id = row["id"]?.GetValue<string>() ?? $"row-{results.Count + 1}";
            string query = row["query"]?.GetValue<string>() ?? throw new InvalidDataException(id + " has no query");
            string tools = row["tools"]?.ToJsonString() ?? throw new InvalidDataException(id + " has no tools");
            JsonNode expected = row["answers"]?.DeepClone() ?? new JsonArray();
            var initClock = Stopwatch.StartNew();
            needle.Init("locale: " + CultureInfo.CurrentUICulture.Name + "; device: phone; assistant: hash", tools);
            initClock.Stop();

            var inferenceClock = Stopwatch.StartNew();
            string raw = needle.Complete(query);
            inferenceClock.Stop();
            JsonNode responseNode = JsonNode.Parse(raw) ?? throw new InvalidDataException(id + " response is not JSON");
            JsonNode actual = responseNode["function_calls"]?.DeepClone() ?? new JsonArray();
            bool exact = JsonNode.DeepEquals(expected, actual);
            results.Add(new BenchmarkResult(
                id,
                row["command"]?.GetValue<string>() ?? "",
                row["language"]?.GetValue<string>() ?? "",
                row["split"]?.GetValue<string>() ?? "",
                row["phase"]?.GetValue<string>() ?? "",
                query,
                expected,
                actual,
                exact,
                initClock.Elapsed.TotalMilliseconds,
                inferenceClock.Elapsed.TotalMilliseconds,
                raw));
            Console.WriteLine($"{results.Count}: {(exact ? "PASS" : "FAIL")} {id} {inferenceClock.Elapsed.TotalMilliseconds:F1} ms");
        }
    }
    totalClock.Stop();

    int exactCount = results.Count(item => item.Exact);
    double[] latencies = results.Select(item => item.InferenceMs).Order().ToArray();
    var report = new
    {
        formatVersion = 1,
        generatedAtUtc = DateTimeOffset.UtcNow,
        engine = enginePath,
        weights = weightsPath,
        dataset = datasetPath,
        cases = results.Count,
        exact = exactCount,
        accuracy = results.Count == 0 ? 0.0 : (double)exactCount / results.Count,
        totalMs = totalClock.Elapsed.TotalMilliseconds,
        medianInferenceMs = Percentile(latencies, 0.5),
        p95InferenceMs = Percentile(latencies, 0.95),
        byLanguage = Breakdown(results, item => item.Language),
        byPhase = Breakdown(results, item => item.Phase),
        results,
    };
    Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
    File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                      new UTF8Encoding(false));
    Console.WriteLine($"exact={exactCount}/{results.Count} accuracy={(results.Count == 0 ? 0 : 100.0 * exactCount / results.Count):F2}% report={reportPath}");
    return 0;
}

static double Percentile(double[] values, double percentile)
{
    if (values.Length == 0) return 0;
    int index = Math.Clamp((int)Math.Ceiling(percentile * values.Length) - 1, 0, values.Length - 1);
    return values[index];
}

static Dictionary<string, object> Breakdown(IEnumerable<BenchmarkResult> results,
                                             Func<BenchmarkResult, string> keySelector) =>
    results.GroupBy(item => string.IsNullOrEmpty(keySelector(item)) ? "unknown" : keySelector(item))
        .OrderBy(group => group.Key)
        .ToDictionary(group => group.Key, group => (object)new
        {
            cases = group.Count(),
            exact = group.Count(item => item.Exact),
            accuracy = (double)group.Count(item => item.Exact) / group.Count(),
        });

internal sealed record BenchmarkResult(
    string Id,
    string Command,
    string Language,
    string Split,
    string Phase,
    string Query,
    JsonNode Expected,
    JsonNode Actual,
    bool Exact,
    double InitMs,
    double InferenceMs,
    string RawResponse);

internal sealed class Needle : IDisposable
{
    private const int BufferSize = 65536;
    private readonly IntPtr _library;
    private readonly InitDelegate _init;
    private readonly CompleteDelegate _complete;
    private readonly ResetDelegate _reset;
    private readonly byte[]? _weightsBlob;
    private GCHandle _weightsPin;

    internal Needle(string path, string? weightsPath = null)
    {
        _library = NativeLibrary.Load(Path.GetFullPath(path));
        try
        {
            _init = Export<InitDelegate>("needle_init");
            _complete = Export<CompleteDelegate>("needle_complete");
            _reset = Export<ResetDelegate>("needle_reset");
            if (!string.IsNullOrEmpty(weightsPath))
            {
                _weightsBlob = File.ReadAllBytes(weightsPath);
                if (_weightsBlob.Length == 0) throw new InvalidOperationException("weights file is empty");
                _weightsPin = GCHandle.Alloc(_weightsBlob, GCHandleType.Pinned);
                int result = Export<LoadDelegate>("needle_load")(
                    _weightsPin.AddrOfPinnedObject(), (ulong)_weightsBlob.LongLength);
                if (result != 0) throw new InvalidOperationException("needle_load failed with code " + result);
            }
        }
        catch
        {
            if (_weightsPin.IsAllocated) _weightsPin.Free();
            NativeLibrary.Free(_library);
            throw;
        }
    }

    internal void Init(string system, string tools)
    {
        using var systemText = new Utf8(system);
        using var toolsText = new Utf8(tools);
        int result = _init(systemText.Pointer, toolsText.Pointer, IntPtr.Zero);
        if (result < 0) throw new InvalidOperationException("needle_init failed with code " + result);
    }

    internal string Complete(string query)
    {
        using var input = new Utf8(query);
        var output = new byte[BufferSize];
        GCHandle pinned = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            int result = _complete(input.Pointer, 256, pinned.AddrOfPinnedObject(), output.Length);
            if (result < 0) throw new InvalidOperationException("needle_complete failed with code " + result);
            int length = Array.IndexOf(output, (byte)0);
            if (length < 0) throw new InvalidOperationException("Needle overflowed its response buffer");
            return Encoding.UTF8.GetString(output, 0, length).TrimEnd('\0');
        }
        finally
        {
            pinned.Free();
        }
    }

    public void Dispose()
    {
        _reset();
        NativeLibrary.Free(_library);
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
        internal Utf8(string value) => Pointer = Marshal.StringToCoTaskMemUTF8(value);
        internal IntPtr Pointer { get; }
        public void Dispose() => Marshal.FreeCoTaskMem(Pointer);
    }
}
