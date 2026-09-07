using System.Text;
using System.Text.Json;
using Hash.Terminal;

// Replay a benchmark report through the mod's own resolver.
//
//   NeedleReplay <commands.json> <values.json> <cases.json> <report.json>
//
// For every case the report answered, this rebuilds the refinement toolset the mod would have built for that
// query and that routed command, hands it the model's own answer, and prints the console line the player would
// have been offered - or the refusal they would have seen instead.
//
// What it cannot see: marks. `#home` is live world state and the committed snapshot has none, so a case whose
// answer is a mark is reported as MARK and left out of the count rather than scored as a failure.

if (args.Length < 4)
{
    Console.Error.WriteLine("usage: NeedleReplay <commands.json> <values.json> <cases.json> <report.json>");
    return 2;
}

var catalogue = new SnapshotCatalogue(args[0], args[1]);
NeedleToolset tools = NeedleToolset.Build(catalogue);

using JsonDocument casesDoc = JsonDocument.Parse(File.ReadAllText(args[2]));
var expectedById = new Dictionary<string, string>(StringComparer.Ordinal);
var queryById = new Dictionary<string, string>(StringComparer.Ordinal);
var unscorable = new HashSet<string>(StringComparer.Ordinal);
foreach (JsonElement one in casesDoc.RootElement.GetProperty("cases").EnumerateArray())
{
    string id = one.GetProperty("id").GetString() ?? "";
    expectedById[id] = one.TryGetProperty("expected", out JsonElement e) ? e.GetString() ?? "" : "";
    queryById[id] = one.TryGetProperty("query", out JsonElement q) ? q.GetString() ?? "" : "";
    if (one.TryGetProperty("unscorable", out _)) unscorable.Add(id);
}

using JsonDocument reportDoc = JsonDocument.Parse(File.ReadAllText(args[3]));
int scored = 0, correct = 0, refused = 0, marks = 0, silent = 0;
var lines = new List<string>();

foreach (JsonElement row in reportDoc.RootElement.GetProperty("results").EnumerateArray())
{
    string id = row.GetProperty("Id").GetString() ?? "";
    if (unscorable.Contains(id)) continue;

    string expected = expectedById.TryGetValue(id, out string want) ? want.Trim() : "";
    string query = queryById.TryGetValue(id, out string q) ? q : "";

    if (expected.Contains('#'))
    {
        marks++;
        lines.Add($"MARK   {id,-28} {expected}");
        continue;
    }

    scored++;

    JsonElement actual = row.GetProperty("Actual");
    if (actual.ValueKind != JsonValueKind.Array || actual.GetArrayLength() == 0)
    {
        if (expected.Length == 0) { correct++; refused++; lines.Add($"ok     {id,-28} (refused)"); }
        else { silent++; lines.Add($"MISS   {id,-28} want {expected}  got (nothing)"); }
        continue;
    }

    if (actual.GetArrayLength() > 1)
    {
        lines.Add($"MULTI  {id,-28} want {expected}  got {actual.GetArrayLength()} calls");
        continue;
    }

    JsonElement call = actual[0];
    string name = call.TryGetProperty("name", out JsonElement n) ? n.GetString() ?? "" : "";

    if (!tools.TryConstrain(name, query, requireAll: false, out NeedleToolset refine, out string toolName))
    {
        lines.Add($"NOCMD  {id,-28} want {expected}  got unknown command {name}");
        continue;
    }

    var sb = new StringBuilder("{\"success\":true,\"function_calls\":[{\"name\":");
    sb.Append(JsonSerializer.Serialize(toolName)).Append(",\"arguments\":");
    sb.Append(call.TryGetProperty("arguments", out JsonElement a) ? a.GetRawText() : "{}");
    sb.Append("}]}");

    NaturalCommandTranslation result = refine.ReadResponse(sb.ToString(), toolName);
    string got = result.Commands.Count > 0 ? result.Commands[0] : "";

    if (got.Length == 0)
    {
        if (expected.Length == 0) { correct++; refused++; lines.Add($"ok     {id,-28} (refused: {result.Error})"); }
        else lines.Add($"REFUSE {id,-28} want {expected}  got refusal: {result.Error}");
        continue;
    }

    if (expected.Length == 0) { lines.Add($"NOISE  {id,-28} want (nothing)  got {got}"); continue; }

    if (string.Equals(Normalise(got), Normalise(expected), StringComparison.OrdinalIgnoreCase))
    {
        correct++;
        lines.Add($"ok     {id,-28} {got}");
    }
    else
    {
        lines.Add($"WRONG  {id,-28} want {expected}  got {got}");
    }
}

foreach (string line in lines) Console.WriteLine(line);
Console.WriteLine();
Console.WriteLine(
    $"{correct}/{scored} as the player would get it   ({refused} correct refusals, {silent} silent, "
    + $"{marks} mark cases not scorable from a snapshot)");
return 0;

static string Normalise(string line) =>
    string.Join(" ", (line ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries));

/// <summary>
/// The committed catalogue snapshot, dressed as the port the mod reads.
///
/// Owns() answers from the snapshot itself: a slot the game offered values for had a provider. A slot that is
/// empty in the file is reported as unowned, which is the one place this differs from a live game - there the
/// difference between "no provider" and "a provider with nothing to offer" is a refusal.
/// </summary>
sealed class SnapshotCatalogue : ICommandCatalogue
{
    private readonly List<CommandInfo> _commands = new();
    private readonly Dictionary<string, List<List<string>>> _values =
        new(StringComparer.OrdinalIgnoreCase);

    internal SnapshotCatalogue(string commandsPath, string valuesPath)
    {
        using JsonDocument commands = JsonDocument.Parse(File.ReadAllText(commandsPath));
        foreach (JsonElement one in commands.RootElement.EnumerateArray())
        {
            _commands.Add(new CommandInfo(
                Text(one, "name"), Text(one, "description"), Text(one, "usage"), Text(one, "signature"),
                Text(one, "source"),
                one.TryGetProperty("vanilla", out JsonElement vanilla) && vanilla.ValueKind == JsonValueKind.True));
        }

        using JsonDocument values = JsonDocument.Parse(File.ReadAllText(valuesPath));
        foreach (JsonProperty command in values.RootElement.EnumerateObject())
        {
            var slots = new List<List<string>>();
            foreach (JsonElement slot in command.Value.EnumerateArray())
            {
                var one = new List<string>();
                foreach (JsonElement value in slot.EnumerateArray())
                {
                    string text = value.GetString();
                    if (!string.IsNullOrWhiteSpace(text)) one.Add(text);
                }

                slots.Add(one);
            }

            _values[command.Name] = slots;
        }
    }

    public IReadOnlyList<CommandInfo> Commands => _commands;

    public IReadOnlyList<ArgValue> ValuesFor(string command, int argIndex)
    {
        if (!_values.TryGetValue(command, out List<List<string>> slots) || argIndex >= slots.Count)
            return Array.Empty<ArgValue>();

        return slots[argIndex].Select(value => new ArgValue(value, "snapshot", true)).ToList();
    }

    public MarkKind KindOf(string command, int argIndex) => MarkKind.None;

    public bool Owns(string command, int argIndex) =>
        _values.TryGetValue(command, out List<List<string>> slots)
        && argIndex < slots.Count && slots[argIndex].Count > 0;

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) ? value.GetString() ?? "" : "";
}
