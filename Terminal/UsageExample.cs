using System.Text;

namespace Hash.Terminal
{
    /// <summary>
    /// Turning the game's example usage into something the player can read, and mining it for choices.
    ///
    /// The game exposes no parameter names at all - a <c>ConsoleCommand</c> carries a word, a sentence and a free
    /// text example, and that is the lot. So `give <item> [quantity]` is not read from anywhere; it is written here,
    /// once, per command. That is a description of the game rather than an invention, which is the only reason a
    /// hand-kept table is the right answer: the alternative is showing the player `give ogkush 5` and letting them
    /// guess which part is the argument.
    ///
    /// Commands not in the table fall back to a heuristic over their own example. It is worse, and it is what every
    /// modded command gets, so it has to be honest rather than clever: name what can be named, and call the rest
    /// `arg1`.
    /// </summary>
    public static class UsageExample
    {
        /// <summary>
        /// The argument shapes worth writing out by hand, one line per vanilla command whose example does not
        /// explain itself.
        ///
        /// Verified against the command list the game registers; a word that disappears from the game simply stops
        /// being looked up, and one that appears falls through to the heuristic until someone adds it here.
        /// </summary>
        private static readonly Dictionary<string, string> Known = new(StringComparer.OrdinalIgnoreCase)
        {
            ["give"] = "give <item> [quantity]",
            ["packageproduct"] = "packageproduct <packaging>",
            ["setdiscovered"] = "setdiscovered <product>",
            ["teleport"] = "teleport <location|property|npc>",
            ["spawnvehicle"] = "spawnvehicle <vehicle>",
            ["setowned"] = "setowned <property|business>",
            ["setunlocked"] = "setunlocked <npc>",
            ["setrelationship"] = "setrelationship <npc> <value>",
            ["addemployee"] = "addemployee <type> <property>",
            ["setquality"] = "setquality <quality>",
            ["setregionunlocked"] = "setregionunlocked <region>",
            ["setqueststate"] = "setqueststate <quest> <state>",
            ["setquestentrystate"] = "setquestentrystate <quest> <entry> <state>",
            ["setvar"] = "setvar <variable> <value>",
            ["bind"] = "bind <key> <command>",
            ["unbind"] = "unbind <key>",
            ["setpoliceignoreplayers"] = "setpoliceignoreplayers <true|false>",
            ["setweather"] = "setweather <weather>",
            ["triggerlightning"] = "triggerlightning [npc|player]",
            ["settime"] = "settime <hhmm>",
            ["changecash"] = "changecash <amount>",
            ["changebalance"] = "changebalance <amount>",
            ["addxp"] = "addxp <amount>",
            ["setstaminareserve"] = "setstaminareserve <amount>",
            ["setmovespeed"] = "setmovespeed <multiplier>",
            ["setjumpforce"] = "setjumpforce <multiplier>",
        };

        /// <summary>Commands whose last argument is genuinely optional, where the example always shows it.</summary>
        private static readonly HashSet<string> OptionalLast = new(StringComparer.OrdinalIgnoreCase)
        {
            "give", "triggerlightning",
        };

        /// <summary>
        /// The line shown above the suggestions: the command and its arguments.
        ///
        /// A command that already writes its own shape into the example - anything containing an angle bracket -
        /// keeps it, because whoever wrote that knew better than any heuristic here.
        /// </summary>
        public static string Signature(string word, string example)
        {
            if (string.IsNullOrEmpty(word)) return "";

            if (!string.IsNullOrEmpty(example) && example.IndexOf('<') >= 0)
                return Collapse(FirstExample(example));

            if (Known.TryGetValue(word, out string known)) return known;

            return FromExample(word, example);
        }

        /// <summary>
        /// The literal choices for one argument, dug out of the shape.
        ///
        /// This is what fills the list for a command nobody wrote a provider for: `setweather` says
        /// `<clear|lightrain|heavyrain>`, so those three are the answer. Only alternatives count - a bare
        /// `<amount>` is a placeholder, not a choice, and offering the word "amount" as a value would be worse
        /// than offering nothing.
        /// </summary>
        public static IEnumerable<string> Literals(string example, string signature, int argIndex)
        {
            string shape = !string.IsNullOrEmpty(signature) ? signature : example;
            if (string.IsNullOrEmpty(shape) || argIndex < 0) yield break;

            List<string> tokens = Split(FirstExample(shape));
            int wanted = argIndex + 1;
            if (wanted >= tokens.Count) yield break;

            string token = tokens[wanted].Trim('<', '>', '[', ']');
            if (token.IndexOf('|') < 0) yield break;

            foreach (string choice in token.Split('|'))
            {
                string one = choice.Trim();
                if (one.Length > 0) yield return one;
            }
        }

        /// <summary>How many arguments the shape describes, for deciding whether the caret has run past the end.</summary>
        public static int ArgumentCount(string signature)
        {
            if (string.IsNullOrEmpty(signature)) return 0;

            return Math.Max(0, Split(signature).Count - 1);
        }

        // ------------------------------------------------------------------------------------------ fallback --

        private static string FromExample(string word, string example)
        {
            List<string> tokens = Split(FirstExample(example));
            if (tokens.Count <= 1) return word.ToLowerInvariant();

            var sb = new StringBuilder(word.ToLowerInvariant());

            for (int i = 1; i < tokens.Count; i++)
            {
                bool last = i == tokens.Count - 1;

                // A trailing number or bool on a command with at least two arguments is nearly always the optional
                // one - a quantity, a multiplier, a flag. Guessing here is safe: the brackets are a hint about
                // whether it may be left off, and getting it wrong costs the player one attempt.
                bool optional = last && tokens.Count >= 3 && (LooksNumeric(tokens[i]) || LooksBoolean(tokens[i]))
                                || last && OptionalLast.Contains(word) && tokens.Count >= 3;

                string name = Name(word, i, tokens[i]);
                sb.Append(' ').Append(optional ? '[' : '<').Append(name).Append(optional ? ']' : '>');
            }

            return sb.ToString();
        }

        private static string Name(string word, int index, string sample)
        {
            if (LooksNumeric(sample)) return index == 1 ? "value" : "amount";
            if (LooksBoolean(sample)) return "true|false";

            switch (word.ToLowerInvariant())
            {
                case "give":
                case "setdiscovered":
                case "packageproduct":
                    return index == 1 ? "item" : "quantity";

                case "teleport":
                case "setowned":
                    return "location";

                case "spawnvehicle":
                    return "vehicle";

                default:
                    return "arg" + index;
            }
        }

        private static bool LooksNumeric(string token) =>
            int.TryParse(token, out _)
            || float.TryParse(token, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out _);

        private static bool LooksBoolean(string token) =>
            string.Equals(token, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(token, "false", StringComparison.OrdinalIgnoreCase);

        /// <summary>Several commands offer more than one example, separated by a comma or the word "or". Only the
        /// first is used; showing two shapes at once explains nothing.</summary>
        private static string FirstExample(string example)
        {
            if (string.IsNullOrEmpty(example)) return "";

            string one = example.Trim().Trim('\'', '"');

            int cut = one.IndexOf(',');
            int or = one.IndexOf(" or ", StringComparison.OrdinalIgnoreCase);
            if (or >= 0 && (cut < 0 || or < cut)) cut = or;

            return (cut > 0 ? one.Substring(0, cut) : one).Trim().Trim('\'', '"');
        }

        private static List<string> Split(string line) =>
            new List<string>((line ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));

        private static string Collapse(string value)
        {
            var sb = new StringBuilder(value.Length);
            bool space = false;

            foreach (char c in value)
            {
                if (c == ' ' || c == '\t')
                {
                    if (!space && sb.Length > 0) sb.Append(' ');
                    space = true;
                    continue;
                }

                sb.Append(c);
                space = false;
            }

            return sb.ToString().TrimEnd();
        }
    }
}
