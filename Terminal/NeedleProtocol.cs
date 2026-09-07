using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hash.Terminal
{
    /// <summary>
    /// A frozen command catalogue in Needle's tool format, plus the map that turns its structured calls back into
    /// console lines. Built on the game thread so providers never have to be touched by the inference worker.
    /// </summary>
    internal sealed class NeedleToolset
    {
        internal const int MaxCalls = 8;
        internal const int MaxEnumValues = 32;
        internal const int MaxEnumCharacters = 80;
        internal const int MaxRouteDescriptionCharacters = 48;
        internal const int MaxFullDescriptionCharacters = 96;

        private readonly Dictionary<string, NeedleTool> _byName;
        private readonly Dictionary<string, NeedleTool> _byWord;
        private readonly bool _routeOnly;

        /// <summary>The request this toolset was built for, or null for the catalogue-wide snapshot that is built
        /// once and reused. It is what lets an answer be checked against the player's own words.</summary>
        private readonly string _query;

        private NeedleToolset(string json, string fingerprint, Dictionary<string, NeedleTool> byName,
                              Dictionary<string, NeedleTool> byWord, bool routeOnly = false, string query = null)
        {
            Json = json;
            Fingerprint = fingerprint;
            _byName = byName;
            _byWord = byWord;
            _routeOnly = routeOnly;
            _query = query;
        }

        internal string Json { get; }

        internal string Fingerprint { get; }

        internal int Count => _byName.Count;

        internal bool RouteOnly => _routeOnly;

        internal static NeedleToolset Build(ICommandCatalogue catalogue, Marks marks = null)
        {
            var commands = catalogue.Commands
                .Where(command => command != null && command.Word.Length > 0 && command.Word != "#")
                .OrderBy(command => command.Word, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var tools = new List<NeedleTool>();
            var usedNames = new HashSet<string>(StringComparer.Ordinal);

            foreach (CommandInfo command in commands)
            {
                string name = UniqueToolName(command.Word, usedNames);
                tools.Add(NeedleTool.Build(name, command, catalogue, marks));
            }

            string json = WriteTools(tools, includeLiveValues: false, requireAll: false, query: null);
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return new NeedleToolset(
                json,
                fingerprint,
                tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal),
                tools.ToDictionary(tool => tool.CommandWord, StringComparer.OrdinalIgnoreCase));
        }

        /// <summary>
        /// First half of OUR two-pass flow - not Needle's. The engine documents one call over the whole
        /// catalogue: it embeds the schemas, retrieves the top few for the query itself, and generates a
        /// complete call. Splitting that into a route and a refinement is a choice this mod made, and the
        /// routing declaration is thinner than the engine ever intended - a real description, no argument
        /// object at all, so the pass can only choose a name.
        ///
        /// It is measured, not assumed: the tuned adapter reaches 78.5 % routing this way, while one call
        /// over all 78 full schemas scored 20/79 on the untuned base and 19/79 on the tuned archive. Keeping
        /// it therefore has evidence behind it; calling it Needle's recommendation did not.
        /// </summary>
        internal NeedleToolset ForRouting()
        {
            List<NeedleTool> tools = _byName.Values.OrderBy(tool => tool.Name, StringComparer.Ordinal).ToList();
            string json = WriteRoutingTools(tools);
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return new NeedleToolset(json, fingerprint, _byName, _byWord, routeOnly: true);
        }

        /// <summary>
        /// Strengthen an explicit command without hard-coding any words. Needle retrieves only five tools from a
        /// large catalogue; repeating the exact live command vocabulary gives its retrieval head an unambiguous
        /// anchor. The single-tool snapshot is a correctness fallback if the broad retriever still misses it.
        /// </summary>
        internal NeedleRequest Prepare(string query)
        {
            string original = query ?? "";
            string leading = LeadingToken(original);

            if (leading.Length == 0 || !_byWord.TryGetValue(leading, out NeedleTool tool))
                return new NeedleRequest(original, null, null, null);

            string routed = "Console command: " + tool.CommandWord + ". User request: " + original;
            NaturalCommandTranslation direct = tool.TryBuildDirect(original, out string line)
                ? new NaturalCommandTranslation(new[] { line }, 1d, proven: true)
                : null;
            return new NeedleRequest(routed, tool.Name, From(tool, original), direct);
        }

        internal NaturalCommandTranslation ReadResponse(string json, string expectedToolName = null)
        {
            if (string.IsNullOrWhiteSpace(json))
                return Failed("the engine returned an empty response");

            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                JsonElement root = document.RootElement;

                double? confidence = Number(root, "confidence");
                string reasoning = root.TryGetProperty("reasoning", out JsonElement reasoningValue)
                                   && reasoningValue.ValueKind == JsonValueKind.String
                    ? reasoningValue.GetString() ?? "" : "";
                double? prefillTps = Number(root, "prefill_tps");
                double? decodeTps = Number(root, "decode_tps");
                double? peakRamMb = Number(root, "peak_ram_mb");

                NaturalCommandTranslation Reject(string error) =>
                    new(Array.Empty<string>(), confidence, error, reasoning: reasoning,
                        prefillTps: prefillTps, decodeTps: decodeTps, peakRamMb: peakRamMb);

                if (root.TryGetProperty("success", out JsonElement success)
                    && success.ValueKind != JsonValueKind.True)
                    return Reject(ReadError(root, "the engine rejected the request"));

                if (confidence is double score)
                {
                    if (score < 0 || score > 1)
                        return Reject("the engine returned confidence outside the 0 to 1 range");
                }

                if (!root.TryGetProperty("function_calls", out JsonElement calls)
                    || calls.ValueKind != JsonValueKind.Array)
                    return Reject("the engine returned an invalid function_calls value");

                if (calls.GetArrayLength() == 0)
                    return new NaturalCommandTranslation(Array.Empty<string>(), confidence, reasoning: reasoning,
                        prefillTps: prefillTps, decodeTps: decodeTps, peakRamMb: peakRamMb);

                if (calls.GetArrayLength() > MaxCalls)
                    return Reject("the engine returned more than eight commands");

                var commands = new List<string>();
                NeedleTool single = null;
                foreach (JsonElement call in calls.EnumerateArray())
                {
                    string name = call.TryGetProperty("name", out JsonElement nameValue)
                        ? nameValue.GetString() ?? "" : "";

                    if (expectedToolName != null && !string.Equals(name, expectedToolName, StringComparison.Ordinal))
                    {
                        string expectedWord = _byName.TryGetValue(expectedToolName, out NeedleTool expected)
                            ? expected.CommandWord : expectedToolName;
                        return Reject("Hash ignored the explicit command " + expectedWord);
                    }

                    if (!_byName.TryGetValue(name, out NeedleTool tool))
                        return Reject("the engine returned an unknown tool: " + (name.Length > 0 ? name : "(empty)"));

                    if (_routeOnly)
                    {
                        commands.Add(tool.CommandWord);
                        continue;
                    }

                    JsonElement arguments = call.TryGetProperty("arguments", out JsonElement found)
                        ? found : default;

                    if (!tool.TryBuild(arguments, out string line, out string error)) return Reject(error);
                    commands.Add(line);
                    single = tool;
                }

                // Only ever for a lone call. Filling per call turns two identical answers into two identical lines,
                // and the session runs both - a player who asked for ten would be given twenty.
                if (commands.Count == 1 && single != null && !_routeOnly && _query != null)
                    commands[0] = single.RepairQuantity(commands[0], _query);

                return new NaturalCommandTranslation(commands, confidence, reasoning: reasoning,
                    prefillTps: prefillTps, decodeTps: decodeTps, peakRamMb: peakRamMb);
            }
            catch (JsonException e)
            {
                return Failed("the engine returned invalid JSON: " + e.Message);
            }
        }

        private static NaturalCommandTranslation Failed(string error) =>
            new NaturalCommandTranslation(Array.Empty<string>(), null, error);

        private static double? Number(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out double number) ? number : null;

        private static string ReadError(JsonElement root, string fallback)
        {
            if (!root.TryGetProperty("error", out JsonElement error) || error.ValueKind != JsonValueKind.String)
                return fallback;

            return error.GetString() ?? fallback;
        }

        private static string UniqueToolName(string word, HashSet<string> used)
        {
            var sb = new StringBuilder();
            foreach (char c in word.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');

            string stem = sb.ToString().Trim('_');
            if (stem.Length == 0 || char.IsDigit(stem[0])) stem = "command_" + stem;
            if (stem.Length == 0) stem = "command";

            string name = stem;
            int suffix = 2;
            while (!used.Add(name)) name = stem + "_" + suffix++;
            return name;
        }

        private static NeedleToolset From(NeedleTool tool, string query)
        {
            var tools = new[] { tool };
            string json = WriteTools(tools, includeLiveValues: true, requireAll: false, query);
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            return new NeedleToolset(
                json,
                fingerprint,
                new Dictionary<string, NeedleTool>(StringComparer.Ordinal) { [tool.Name] = tool },
                new Dictionary<string, NeedleTool>(StringComparer.OrdinalIgnoreCase) { [tool.CommandWord] = tool },
                query: query);
        }

        /// <summary>
        /// Build the second-pass schema. It contains only the command selected by the first pass and, unlike
        /// the broad retrieval index, embeds every value currently supplied for its arguments - which the
        /// engine's own retrieval cannot do, because it never sees the live item ids we do not declare. Nothing here is command-specific: mod commands and late provider values take the same
        /// path. Strict mode is diagnostic only and makes optional arguments mandatory so a later pass can measure
        /// whether omission, rather than routing, caused a miss.
        /// </summary>
        internal bool TryConstrain(string commandWord, string query, bool requireAll, out NeedleToolset constrained,
                                   out string toolName)
        {
            constrained = null;
            toolName = null;
            if (string.IsNullOrWhiteSpace(commandWord)
                || !_byWord.TryGetValue(commandWord.Trim(), out NeedleTool tool)) return false;

            var tools = new[] { tool };
            string json = WriteTools(tools, includeLiveValues: true, requireAll: requireAll, query);
            string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json))).ToLowerInvariant();
            constrained = new NeedleToolset(
                json,
                fingerprint,
                new Dictionary<string, NeedleTool>(StringComparer.Ordinal) { [tool.Name] = tool },
                new Dictionary<string, NeedleTool>(StringComparer.OrdinalIgnoreCase) { [tool.CommandWord] = tool },
                query: query);
            toolName = tool.Name;
            return true;
        }

        private static string LeadingToken(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return "";

            int start = 0;
            while (start < query.Length && char.IsWhiteSpace(query[start])) start++;

            int end = start;
            while (end < query.Length && !char.IsWhiteSpace(query[end])) end++;

            return query.Substring(start, end - start).Trim(',', '.', ':', '!', '?');
        }

        private static string WriteTools(IReadOnlyList<NeedleTool> tools, bool includeLiveValues, bool requireAll,
                                         string query)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (NeedleTool tool in tools) tool.Write(writer, includeLiveValues, requireAll, query);
                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private static string WriteRoutingTools(IReadOnlyList<NeedleTool> tools)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (NeedleTool tool in tools) tool.WriteRoute(writer);
                writer.WriteEndArray();
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    internal readonly struct NeedleRequest
    {
        internal NeedleRequest(string text, string expectedToolName, NeedleToolset fallback,
                               NaturalCommandTranslation direct)
        {
            Text = text ?? "";
            ExpectedToolName = expectedToolName;
            Fallback = fallback;
            Direct = direct;
        }

        internal string Text { get; }

        internal string ExpectedToolName { get; }

        internal NeedleToolset Fallback { get; }

        internal NaturalCommandTranslation Direct { get; }
    }

    internal sealed class NeedleTool
    {
        private readonly CommandInfo _command;
        private readonly IReadOnlyList<NeedleArgument> _arguments;

        private NeedleTool(string name, CommandInfo command, IReadOnlyList<NeedleArgument> arguments)
        {
            Name = name;
            _command = command;
            _arguments = arguments;
        }

        internal string Name { get; }

        internal string CommandWord => _command.Word;

        internal static NeedleTool Build(string name, CommandInfo command, ICommandCatalogue catalogue,
                                         Marks marks = null)
        {
            List<string> shape = CommandLine.Tokenise(command.Signature);
            List<string> example = CommandLine.Tokenise(command.Usage);
            var arguments = new List<NeedleArgument>();

            for (int i = 1; i < shape.Count; i++)
            {
                string token = shape[i];
                bool required = token.StartsWith("<", StringComparison.Ordinal);
                string label = token.Trim('<', '>', '[', ']');
                IReadOnlyList<string> values = catalogue.ValuesFor(command.Word, i - 1)
                    .Select(value => value.Value)
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                string exampleToken = i < example.Count ? example[i] : "";
                MarkKind kind = catalogue.KindOf(command.Word, i - 1);
                arguments.Add(new NeedleArgument(i, label, required, catalogue.Owns(command.Word, i - 1),
                    kind, values, exampleToken, ResolvingMarks(marks, kind)));
            }

            return new NeedleTool(name, command, arguments);
        }

        /// <summary>
        /// The mark words that point at something right now AND fit this slot - the filter
        /// <see cref="Suggestions"/> already applies when it offers them to a player typing by hand.
        ///
        /// Declaring them is the difference between a word the model can pick and one it has to invent. Seven
        /// benchmark cases want `teleport #home`; the model wrote `home`, which is not a teleport target, or
        /// reached for a different command entirely. A mark that does not currently resolve is not offered, so
        /// nothing here can propose `#car` to a player who is on foot.
        /// </summary>
        private static IReadOnlyList<string> ResolvingMarks(Marks marks, MarkKind wanted)
        {
            if (marks == null || wanted == MarkKind.None) return Array.Empty<string>();

            var words = new List<string>();
            foreach (string word in Marks.Words)
            {
                Mark mark = marks.Resolve(word);
                if (!mark.Exists) continue;
                if (mark.Kind != MarkKind.Any && wanted != MarkKind.Any && mark.Kind != wanted) continue;
                words.Add(word);
            }

            return words;
        }

        internal void Write(Utf8JsonWriter writer, bool includeLiveValues, bool requireAll, string query)
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);
            writer.WriteString("description", Description(routeOnly: false));

            writer.WritePropertyName("parameters");
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();
            foreach (NeedleArgument argument in _arguments) argument.Write(writer, includeLiveValues, query);
            writer.WriteEndObject();
            writer.WriteBoolean("additionalProperties", false);

            writer.WritePropertyName("required");
            writer.WriteStartArray();
            foreach (NeedleArgument argument in _arguments)
                if (requireAll || argument.Required) writer.WriteStringValue(argument.Name);
            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        internal void WriteRoute(Utf8JsonWriter writer)
        {
            writer.WriteStartObject();
            writer.WriteString("name", Name);
            writer.WriteString("description", Description(routeOnly: true));
            writer.WriteEndObject();
        }

        private string Description(bool routeOnly)
        {
            string description = string.IsNullOrWhiteSpace(_command.Description)
                ? "Run the " + _command.Word + " console command."
                : _command.Description;
            description = description.Trim().TrimEnd('.');
            int limit = routeOnly
                ? NeedleToolset.MaxRouteDescriptionCharacters
                : NeedleToolset.MaxFullDescriptionCharacters;
            if (description.Length <= limit) return description;

            string first = description.Split('.')[0].Trim();
            return first.Length > 0 && first.Length <= limit
                ? first
                : description.Substring(0, limit).TrimEnd(' ', ',', ';', ':');
        }

        /// <summary>
        /// Resolve command-shaped natural input without asking the model when the live signature proves every
        /// meaningful token. This is intentionally conservative: anything unexplained falls back to Needle.
        /// </summary>
        internal bool TryBuildDirect(string query, out string line)
        {
            line = null;
            List<string> tokens = CommandLine.Tokenise(query ?? "");
            if (tokens.Count == 0 || !string.Equals(tokens[0], _command.Word, StringComparison.OrdinalIgnoreCase))
                return false;

            var claimed = new bool[tokens.Count];
            claimed[0] = true;
            var resolved = new string[_arguments.Count];

            // Domain values first: they may span words (`speed grow`) and must claim those words before scalar
            // slots look for a number or a free token elsewhere in the sentence.
            for (int i = 0; i < _arguments.Count; i++)
            {
                NeedleArgument argument = _arguments[i];
                if (!argument.HasValues) continue;

                if (!argument.TryMatch(tokens, claimed, out string value, out int start, out int length)) continue;
                resolved[i] = value;
                for (int at = start; at < start + length; at++) claimed[at] = true;
            }

            // Typed scalar slots are independent of word order. This is what makes `give me 10 ogkush` map the
            // number to quantity even though quantity is the command's second argument.
            for (int i = 0; i < _arguments.Count; i++)
            {
                NeedleArgument argument = _arguments[i];
                if (resolved[i] != null || argument.HasValues) continue;

                for (int at = 1; at < tokens.Count; at++)
                {
                    if (claimed[at] || !argument.TryDirectScalar(tokens[at], out string value)) continue;
                    resolved[i] = value;
                    claimed[at] = true;
                    break;
                }
            }

            for (int at = 1; at < tokens.Count; at++)
                if (!claimed[at] && !NaturalFiller(tokens[at])) return false;

            var built = new List<string> { _command.Word };
            bool omitted = false;
            for (int i = 0; i < _arguments.Count; i++)
            {
                if (resolved[i] == null)
                {
                    if (_arguments[i].Required) return false;
                    omitted = true;
                    continue;
                }

                if (omitted) return false;
                built.Add(ConsoleToken(resolved[i]));
            }

            line = string.Join(" ", built);
            return true;
        }

        /// <summary>
        /// Put back a quantity the player stated and the answer dropped, and take away one they never asked for.
        ///
        /// The model names the item and omits the count: "give me 10 og kush" comes back as `give ogkush`, in every
        /// language, with or without the adapter. Declaring the argument required recovers it and invents a count
        /// where none was stated, which is worse. The mod can see what the model cannot: the player's own sentence
        /// is still here, and a number in it that no argument claimed is the count.
        ///
        /// <para>Three gates, and each one is a bug caught before it shipped. Only a command whose shape was written
        /// out by hand (<see cref="UsageExample.IsDeclared"/>) - elsewhere both "optional" and the label "amount"
        /// are one heuristic's guess over a free-text example, and one such amount is a 0..1 fraction whose absence
        /// means the maximum. Only one free number, because two mean the sentence is about something else as well.
        /// And never an overwrite, so a count the model did get right is never second-guessed.</para>
        /// </summary>
        internal string RepairQuantity(string line, string query)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(query)) return line;
            if (!UsageExample.IsDeclared(_command.Word)) return line;

            int slot = -1;
            for (int i = 0; i < _arguments.Count; i++)
            {
                if (_arguments[i].Required || !_arguments[i].IsNumber) continue;
                if (slot >= 0) return line; // more than one optional number: nothing here can tell them apart
                slot = i;
            }

            if (slot < 0) return line;

            List<string> built = CommandLine.Tokenise(line);
            if (built.Count > slot + 2) return line; // the slot is not the last thing on the line

            string stated = StatedQuantity(query, built);

            if (built.Count == slot + 2)
            {
                // The model supplied one. Leave it alone unless it is a count of nothing the player never asked
                // for: `give mixingstation 0` is a line the console accepts and that does nothing.
                if (!double.TryParse(built[slot + 1], NumberStyles.Float, CultureInfo.InvariantCulture,
                                     out double supplied) || supplied > 0 || stated != null)
                    return line;

                built.RemoveAt(slot + 1);
                return string.Join(" ", built);
            }

            if (built.Count != slot + 1 || stated == null) return line;

            built.Add(stated);
            return string.Join(" ", built);
        }

        /// <summary>
        /// The one number in the player's sentence that no argument on this line already accounts for.
        ///
        /// Claims the words the resolved values occupy first, exactly as <see cref="TryBuildDirect"/> does, so
        /// "give me 5 og kush seed" does not read the 5 out of an item name and an id with a digit in it does not
        /// look like a count. Null when nothing is left, when two numbers are, or when the only candidate is 1 or
        /// less - a lone "1" is far more often part of a name than a count.
        /// </summary>
        private string StatedQuantity(string query, List<string> built)
        {
            List<string> tokens = CommandLine.Tokenise(query);
            if (tokens.Count == 0) return null;

            var claimed = new bool[tokens.Count];
            for (int i = 0; i < _arguments.Count && i + 1 < built.Count; i++)
            {
                NeedleArgument argument = _arguments[i];
                if (!argument.HasValues) continue;
                if (!argument.TryMatch(tokens, claimed, out string value, out int start, out int length)) continue;
                if (!string.Equals(value, built[i + 1], StringComparison.OrdinalIgnoreCase)) continue;
                for (int at = start; at < start + length; at++) claimed[at] = true;
            }

            string found = null;
            for (int at = 0; at < tokens.Count; at++)
            {
                if (claimed[at]) continue;

                string token = tokens[at].Trim(',', '.', '!', '?', ';', ':');
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    && !NumberWords.TryGetValue(NeedleArgument.Normal(token), out number))
                    continue;

                if (number <= 1) continue;
                if (found != null) return null; // two candidates, and nothing declared says which is the count
                found = number.ToString(CultureInfo.InvariantCulture);
            }

            return found;
        }

        /// <summary>
        /// Counts a player writes as a word, in the four languages the corpus covers.
        ///
        /// Small on purpose: past twelve people write digits, and every entry here is a word that could otherwise
        /// be an item name in another language. A fifth language falls back to digits, which is today's behaviour.
        /// </summary>
        private static readonly Dictionary<string, int> NumberWords = new(StringComparer.Ordinal)
        {
            ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
            ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["dozen"] = 12,
            ["zwei"] = 2, ["drei"] = 3, ["vier"] = 4, ["fuenf"] = 5, ["funf"] = 5, ["sechs"] = 6, ["sieben"] = 7,
            ["acht"] = 8, ["neun"] = 9, ["zehn"] = 10, ["elf"] = 11, ["zwoelf"] = 12, ["zwolf"] = 12,
            ["dos"] = 2, ["tres"] = 3, ["cuatro"] = 4, ["cinco"] = 5, ["seis"] = 6, ["siete"] = 7, ["ocho"] = 8,
            ["nueve"] = 9, ["diez"] = 10, ["once"] = 11, ["doce"] = 12,
            ["deux"] = 2, ["trois"] = 3, ["quatre"] = 4, ["cinq"] = 5, ["sept"] = 7, ["huit"] = 8, ["neuf"] = 9,
            ["dix"] = 10, ["onze"] = 11, ["douze"] = 12,
        };

        internal bool TryBuild(JsonElement values, out string line, out string error)
        {
            line = null;
            error = null;

            if (values.ValueKind != JsonValueKind.Object &&
                !(_arguments.Count == 0 && values.ValueKind == JsonValueKind.Undefined))
            {
                error = _command.Word + ": Hash returned no arguments";
                return false;
            }

            if (values.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty supplied in values.EnumerateObject())
                {
                    if (_arguments.Any(argument => argument.Name == supplied.Name)) continue;
                    error = _command.Word + ": Hash returned an unknown argument: " + supplied.Name;
                    return false;
                }
            }

            var built = new List<string> { _command.Word };
            bool omitted = false;

            foreach (NeedleArgument argument in _arguments)
            {
                if (!values.TryGetProperty(argument.Name, out JsonElement value)
                    || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                {
                    if (argument.Required)
                    {
                        error = _command.Word + ": Hash omitted " + argument.Label;
                        return false;
                    }

                    omitted = true;
                    continue;
                }

                if (omitted)
                {
                    error = _command.Word + ": Hash skipped an argument before " + argument.Label;
                    return false;
                }

                if (!argument.TryResolve(value, out string resolved, out error)) return false;
                built.Add(ConsoleToken(resolved));
            }

            line = string.Join(" ", built);
            return true;
        }

        private static string ConsoleToken(string value) =>
            value.Any(char.IsWhiteSpace) ? "\"" + value + "\"" : value;

        internal static bool NaturalFiller(string token)
        {
            string word = NeedleArgument.Normal(token);
            return word is "a" or "an" or "at" or "by" or "for" or "me" or "my" or "of" or "please" or "some"
                or "the" or "to" or "with";
        }
    }

    internal sealed class NeedleArgument
    {
        private readonly bool _owned;
        private readonly MarkKind _markKind;
        private readonly IReadOnlyList<string> _values;
        private readonly IReadOnlyList<string> _schemaValues;
        private readonly IReadOnlyList<string> _markWords;
        private readonly string _jsonType;
        private readonly bool _time;

        internal NeedleArgument(int position, string label, bool required, bool owned, MarkKind markKind,
                                IReadOnlyList<string> values, string exampleToken,
                                IReadOnlyList<string> markWords = null)
        {
            Name = "arg" + position;
            Label = string.IsNullOrWhiteSpace(label) ? Name : label;
            Required = required;
            _owned = owned;
            _markKind = markKind;
            _values = values ?? Array.Empty<string>();
            _schemaValues = Literals(exampleToken);
            _markWords = markWords ?? Array.Empty<string>();
            _time = TimeWords.Owns(Label);
            _jsonType = TypeOf(Label);
        }

        internal string Name { get; }

        internal string Label { get; }

        internal bool Required { get; }

        internal bool HasValues => _values.Count > 0;

        internal bool IsNumber => _jsonType == "number";

        internal void Write(Utf8JsonWriter writer, bool includeLiveValues, string query)
        {
            writer.WritePropertyName(Name);
            writer.WriteStartObject();
            writer.WriteString("type", _jsonType);

            // "hhmm" beside a list of words would ask for two different answers at once.
            string description = _time ? "time of day" : Label.Replace('|', ' ');
            if (_markKind != MarkKind.None)
                description += "; #=current";
            writer.WriteString("description", description);

            IReadOnlyList<string> choices = _time ? TimeWords.Choices(query)
                : includeLiveValues && _values.Count > 0 ? RelevantValues(query)
                : _schemaValues;

            // Marks go last, and that position was measured. A model not shown them writes the bare word instead
            // - `teleport home`, which is not a place - so they have to be offered. Put in FRONT they displace the
            // catalogue values in a slot where the catalogue is the answer: `give` lost four cases to
            // setmovespeed, setvar and bind when `#`, `#hand`, `#last` and `#it` took the head of its list. Last,
            // they take only budget nothing else wanted, which is exactly the case where the enum was empty.
            if (includeLiveValues && _markWords.Count > 0 && _jsonType == "string")
            {
                var offered = new List<string>(choices);
                foreach (string word in _markWords)
                    if (!offered.Contains(word, StringComparer.OrdinalIgnoreCase)) offered.Add(word);
                choices = offered;
            }

            if (_jsonType == "string" && choices.Count > 0)
            {
                writer.WritePropertyName("enum");
                writer.WriteStartArray();
                foreach (string value in choices) writer.WriteStringValue(value);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        private IReadOnlyList<string> RelevantValues(string query)
        {
            if (string.IsNullOrWhiteSpace(query)) return Array.Empty<string>();

            List<string> tokens = CommandLine.Tokenise(query);
            var phrases = new List<string>();
            for (int start = 0; start < tokens.Count; start++)
            for (int count = 1; count <= 6 && start + count <= tokens.Count; count++)
            {
                List<string> span = tokens.Skip(start).Take(count).ToList();

                // A span of nothing but filler carries no information to rank against, and ranking it anyway is
                // not harmless: "me" is a prefix of meth and megabean, "a" of albert_hoover, and a prefix scores
                // 4000. "give me 4 grandaddy seed" was offered meth, megabean and metalsign, and "llevame a los
                // muelles" was offered albert_hoover - an exact catalogue value, so TryResolveText accepts it in
                // silence and the player asking for the docks is teleported to a dealer's house.
                if (span.All(NeedleTool.NaturalFiller)) continue;

                // One letter ranks nothing and displaces everything: "I" is a prefix of iodine, which scores the
                // same 4000 as "mixing" does against mixingstation and sorts ahead of it for being shorter.
                // FuzzyMatcher already refuses a one-character subsequence for the same reason.
                string phrase = Normal(string.Join(" ", span));
                if (phrase.Length > 1 && !phrase.All(char.IsDigit)) phrases.Add(phrase);
            }

            var scored = _values.Select(value => new
                {
                    Value = value,
                    Score = phrases.Count == 0 ? 0 : phrases.Max(phrase => CandidateScore(value, phrase)),
                })
                .ToList();

            IEnumerable<string> ranked = scored
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Value.Length)
                .ThenBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase)
                .Select(candidate => candidate.Value);

            // A slot whose whole vocabulary fits is not a ranking problem. Offering only what matched a word
            // the player typed left the list short and sometimes EMPTY - "make it sunny" was answered with a
            // choice of heavyrain and lightrain, and `clear`, one of setweather's three possible values, was
            // never on it. The fill stops where ranking starts to matter: padding a thousand-item catalogue
            // with whatever sorts first is the original bug, the one that offered "acid, acunit, addy".
            if (_values.Count <= NeedleToolset.MaxEnumValues
                && _values.Sum(value => value.Length) <= NeedleToolset.MaxEnumCharacters)
            {
                ranked = ranked.Concat(scored.Where(candidate => candidate.Score == 0)
                                             .Select(candidate => candidate.Value));
            }

            var selected = new List<string>();
            int characters = 0;
            foreach (string value in ranked)
            {
                int next = characters + value.Length;
                if (selected.Count > 0 && next > NeedleToolset.MaxEnumCharacters) continue;
                selected.Add(value);
                characters = next;
                if (selected.Count >= NeedleToolset.MaxEnumValues) break;
            }

            return selected;
        }

        internal bool TryResolve(JsonElement value, out string resolved, out string error)
        {
            resolved = Scalar(value);
            error = null;

            if (resolved == null)
            {
                error = Label + ": Hash returned a compound value where one console argument was expected";
                return false;
            }

            return TryResolveText(resolved, out resolved, out error);
        }

        internal bool TryDirectScalar(string token, out string resolved)
        {
            resolved = null;
            string value = (token ?? "").Trim();

            if (_time) return TimeWords.TryToken(value, out resolved);

            if (_jsonType == "number")
            {
                if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return false;
                resolved = value;
                return true;
            }

            if (_jsonType == "boolean")
            {
                if (value.Equals("on", StringComparison.OrdinalIgnoreCase)) value = "true";
                if (value.Equals("off", StringComparison.OrdinalIgnoreCase)) value = "false";
                if (!bool.TryParse(value, out bool boolean)) return false;
                resolved = boolean ? "true" : "false";
                return true;
            }

            if (_markKind != MarkKind.None && Marks.IsWord(value))
            {
                resolved = value;
                return true;
            }

            // A free string is only safe when it is one token. Provider-backed strings are matched in phrases by
            // TryMatch so an arbitrary word cannot steal a live-value slot.
            if (_values.Count > 0 || value.Any(char.IsWhiteSpace)) return false;
            return TryResolveText(value, out resolved, out _);
        }

        internal bool TryMatch(IReadOnlyList<string> tokens, bool[] claimed,
                               out string resolved, out int start, out int length)
        {
            resolved = null;
            start = -1;
            length = 0;
            int best = 0;
            bool ambiguous = false;

            for (int from = 1; from < tokens.Count; from++)
            {
                if (claimed[from]) continue;

                for (int count = 1; count <= 6 && from + count <= tokens.Count; count++)
                {
                    bool blocked = false;
                    for (int at = from; at < from + count; at++)
                        if (claimed[at]) { blocked = true; break; }
                    if (blocked) break;

                    string phrase = string.Join(" ", tokens.Skip(from).Take(count));
                    if (!TryCandidate(phrase, out string candidate, out int candidateScore)) continue;

                    // Never let typo tolerance absorb a quantity into a textual catalogue value. For example,
                    // `5 og kush seed` must resolve the item from `og kush seed` and leave `5` for the amount.
                    bool phraseHasNumber = tokens.Skip(from).Take(count).Any(token =>
                        double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
                    if (phraseHasNumber && !Normal(candidate).Any(char.IsDigit)) continue;

                    int score = (count * 10000) + candidateScore;
                    if (score < best) continue;
                    if (score == best && !string.Equals(resolved, candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        ambiguous = true;
                        continue;
                    }

                    best = score;
                    resolved = candidate;
                    start = from;
                    length = count;
                    ambiguous = false;
                }
            }

            return best > 0 && !ambiguous;
        }

        private bool TryResolveText(string value, out string resolved, out string error)
        {
            resolved = (value ?? "").Trim();
            error = null;

            if (resolved.Length == 0)
            {
                error = Label + ": Hash returned an empty argument";
                return false;
            }

            if (resolved.IndexOfAny(new[] { '\r', '\n', '\0', '"', ';' }) >= 0)
            {
                error = Label + ": '" + resolved + "' is not one console token";
                return false;
            }

            if (Marks.IsWord(resolved)) return true; // MarkExpansion performs the kind and existence checks later.

            if (_time)
            {
                if (TimeWords.TryToken(resolved, out string reading)) { resolved = reading; return true; }
                error = Label + ": '" + resolved + "' is not a time";
                return false;
            }

            if (_values.Count == 0)
            {
                if (_owned)
                {
                    error = Label + ": there are no valid values in the current game state";
                    return false;
                }

                if (resolved.Any(char.IsWhiteSpace))
                {
                    error = Label + ": '" + resolved + "' is not one console token";
                    return false;
                }

                return true;
            }

            string requested = resolved;
            string exact = _values.FirstOrDefault(candidate =>
                string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase));
            if (exact != null) { resolved = exact; return true; }

            if (TryCandidate(requested, out string candidate, out _))
            {
                resolved = candidate;
                return true;
            }

            string query = Normal(requested);
            var hits = _values.Where(value => CandidateScore(value, query) > 0).Take(5).ToList();
            error = hits.Count == 0
                ? Label + ": '" + resolved + "' matches no current value"
                : Label + ": '" + resolved + "' is ambiguous (" + string.Join(", ", hits.Take(4)) + ")";
            return false;
        }

        private bool TryCandidate(string requested, out string resolved, out int score)
        {
            string query = Normal(requested);
            int best = 0;
            var hits = new List<string>();

            foreach (string candidate in _values)
            {
                int candidateScore = CandidateScore(candidate, query);
                if (candidateScore < best) continue;
                if (candidateScore > best) { best = candidateScore; hits.Clear(); }
                if (candidateScore > 0) hits.Add(candidate);
            }

            resolved = hits.Count == 1 ? hits[0] : null;
            score = best;
            return resolved != null;
        }

        private static string Scalar(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };

        private static string TypeOf(string label)
        {
            if (TimeWords.Owns(label)) return "string";

            string one = label.ToLowerInvariant();
            if (one == "true|false" || one == "false|true") return "boolean";

            string[] numeric = { "amount", "quantity", "value", "minutes", "hhmm", "scale", "multiplier", "intensity" };
            return numeric.Any(word => one.Contains(word, StringComparison.Ordinal)) ? "number" : "string";
        }

        private static IReadOnlyList<string> Literals(string label)
        {
            string one = (label ?? "").Trim('<', '>', '[', ']');
            string[] values = one.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return values.Length > 1 ? values.Distinct(StringComparer.OrdinalIgnoreCase).ToList() : Array.Empty<string>();
        }

        private static int CandidateScore(string candidate, string query)
        {
            string normalized = Normal(candidate);
            string singular = query.EndsWith("s", StringComparison.Ordinal) ? query.Substring(0, query.Length - 1) : query;
            MatchResult match = FuzzyMatcher.Match(normalized, query);

            if (normalized == singular || query.StartsWith(normalized, StringComparison.Ordinal)) return 4500;
            if (match.Kind >= MatchKind.Substring) return match.Score;

            // One missing/extra character in a substantial ID is a typo, not fuzzy guesswork (`grandaddy` vs
            // `granddaddy`). Short subsequences stay rejected because they match far too much of a 1,000-item list.
            if (query.Length >= 6 && Math.Abs(normalized.Length - query.Length) <= 1
                && EditDistanceAtMostOne(normalized, query)) return 3500;

            // Accept a substantial omitted-word match only when the caller can prove it is unique. This covers
            // IDs such as `granddaddy purple seed` from `grandaddy seed`, while short/generic subsequences remain
            // delegated to Needle instead of being guessed deterministically.
            if (match.Kind == MatchKind.Subsequence && query.Length >= 8
                && query.Length * 2 >= normalized.Length)
                return 2500 - Math.Min(normalized.Length - query.Length, 499);

            return 0;
        }

        private static bool EditDistanceAtMostOne(string left, string right)
        {
            if (left == right) return true;
            if (Math.Abs(left.Length - right.Length) > 1) return false;

            if (left.Length == right.Length)
            {
                int changes = 0;
                for (int i = 0; i < left.Length; i++)
                    if (left[i] != right[i] && ++changes > 1) return false;
                return true;
            }

            string longer = left.Length > right.Length ? left : right;
            string shorter = left.Length > right.Length ? right : left;
            int a = 0, b = 0, skipped = 0;
            while (a < longer.Length && b < shorter.Length)
            {
                if (longer[a] == shorter[b]) { a++; b++; continue; }
                if (++skipped > 1) return false;
                a++;
            }
            return true;
        }

        internal static string Normal(string value)
        {
            var sb = new StringBuilder();
            foreach (char c in value ?? "") if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

    }
}
