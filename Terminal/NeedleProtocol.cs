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

        private NeedleToolset(string json, string fingerprint, Dictionary<string, NeedleTool> byName,
                              Dictionary<string, NeedleTool> byWord, bool routeOnly = false)
        {
            Json = json;
            Fingerprint = fingerprint;
            _byName = byName;
            _byWord = byWord;
            _routeOnly = routeOnly;
        }

        internal string Json { get; }

        internal string Fingerprint { get; }

        internal int Count => _byName.Count;

        internal bool RouteOnly => _routeOnly;

        internal static NeedleToolset Build(ICommandCatalogue catalogue)
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
                tools.Add(NeedleTool.Build(name, command, catalogue));
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
                }

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
                new Dictionary<string, NeedleTool>(StringComparer.OrdinalIgnoreCase) { [tool.CommandWord] = tool });
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
                new Dictionary<string, NeedleTool>(StringComparer.OrdinalIgnoreCase) { [tool.CommandWord] = tool });
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

        internal static NeedleTool Build(string name, CommandInfo command, ICommandCatalogue catalogue)
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
                arguments.Add(new NeedleArgument(i, label, required, catalogue.Owns(command.Word, i - 1),
                    catalogue.KindOf(command.Word, i - 1), values, exampleToken));
            }

            return new NeedleTool(name, command, arguments);
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

        private static bool NaturalFiller(string token)
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
        private readonly string _jsonType;

        internal NeedleArgument(int position, string label, bool required, bool owned, MarkKind markKind,
                                IReadOnlyList<string> values, string exampleToken)
        {
            Name = "arg" + position;
            Label = string.IsNullOrWhiteSpace(label) ? Name : label;
            Required = required;
            _owned = owned;
            _markKind = markKind;
            _values = values ?? Array.Empty<string>();
            _schemaValues = Literals(exampleToken);
            _jsonType = TypeOf(Label);
        }

        internal string Name { get; }

        internal string Label { get; }

        internal bool Required { get; }

        internal bool HasValues => _values.Count > 0;

        internal void Write(Utf8JsonWriter writer, bool includeLiveValues, string query)
        {
            writer.WritePropertyName(Name);
            writer.WriteStartObject();
            writer.WriteString("type", _jsonType);

            string description = Label.Replace('|', ' ');
            if (_markKind != MarkKind.None)
                description += "; #=current";
            writer.WriteString("description", description);

            IReadOnlyList<string> choices = includeLiveValues && _values.Count > 0
                ? RelevantValues(query)
                : _schemaValues;
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
                string phrase = Normal(string.Join(" ", tokens.Skip(start).Take(count)));
                if (phrase.Length > 0 && !phrase.All(char.IsDigit)) phrases.Add(phrase);
            }

            var ranked = _values.Select(value => new
                {
                    Value = value,
                    Score = phrases.Count == 0 ? 0 : phrases.Max(phrase => CandidateScore(value, phrase)),
                })
                .Where(candidate => candidate.Score > 0)
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.Value.Length)
                .ThenBy(candidate => candidate.Value, StringComparer.OrdinalIgnoreCase);

            var selected = new List<string>();
            int characters = 0;
            foreach (var candidate in ranked)
            {
                int next = characters + candidate.Value.Length;
                if (selected.Count > 0 && next > NeedleToolset.MaxEnumCharacters) continue;
                selected.Add(candidate.Value);
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
