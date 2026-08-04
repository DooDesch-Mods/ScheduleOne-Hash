namespace Hash.Terminal
{
    /// <summary>
    /// What each command is FOR, so `help` can be read by someone who does not already know the answer.
    ///
    /// Sixty-five words in alphabetical order is a list only useful to a player who could have typed the word
    /// anyway. Grouping by task is what every large CLI settled on - `git help` opens with "the most commonly used
    /// commands" under headings, not with its full index - because the question is "how do I change the weather",
    /// not "what starts with s".
    ///
    /// <para>The groups double as pages: `help world` lists one of them with descriptions, which is the step
    /// between "help showed me a word I did not know" and "help &lt;word&gt;".</para>
    ///
    /// <para>The grouping is written down here rather than derived, for the same reason the argument shapes are: the
    /// game says nothing about what its commands are for. A command that is not listed still appears, under
    /// <see cref="Other"/> - so a new game version adds commands to the bottom of the list instead of hiding
    /// them.</para>
    /// </summary>
    public static class HelpTopics
    {
        /// <summary>Where a command nobody has classified ends up.</summary>
        public const string Other = "other";

        /// <summary>Where the terminal's own commands go.</summary>
        public const string Terminal = "terminal";

        /// <summary>
        /// The headings, in the order they are printed.
        ///
        /// Ordered by how often a player wants them rather than alphabetically, which is why the map in `help` reads
        /// as a sentence about the game rather than as an index.
        /// </summary>
        public static readonly (string Topic, string[] Words)[] Groups =
        {
            ("items", new[]
            {
                "give", "setdiscovered", "setquality", "setquantity", "packageproduct", "clearinventory",
                "growplants",
            }),
            ("money", new[] { "changecash", "changebalance", "addxp" }),
            ("world", new[]
            {
                "settime", "setdayduration", "settimescale", "setweather", "triggerlightning",
                "triggerdistantthunder", "cleartrash", "forcesleep",
            }),
            ("places", new[] { "teleport", "setowned", "setregionunlocked", "spawnvehicle" }),
            ("people", new[]
            {
                "setunlocked", "setrelationship", "addemployee", "setemotion", "destroynpcs", "disablenpcs",
                "disablenpcasset",
            }),
            ("police", new[]
            {
                "raisewanted", "lowerwanted", "clearwanted", "setlawintensity", "setpoliceignoreplayers",
            }),
            ("player", new[]
            {
                "sethealth", "setstaminareserve", "setmovespeed", "setjumpforce", "setgravitymultiplier", "freecam",
            }),
            ("quests", new[] { "setqueststate", "setquestentrystate", "setvar", "endtutorial", "playcutscene" }),

            // Below the fold of plain `help`.
            ("keys", new[] { "bind", "unbind", "clearbinds" }),
            ("display", new[] { "hideui", "showfps", "hidefps", "enable", "disable" }),
            ("graphics", new[]
            {
                "enableinstancing", "disableinstancing", "enableocclusionculling", "disableocclusionculling",
                "enablephysics", "disablephysics", "enableterrain", "disableterrain", "disablemeshes",
            }),
            ("game", new[] { "save", "quit" }),
            ("dev", new[] { "npcworkbench", "presentationworkbench" }),
        };

        /// <summary>
        /// The handful `help` opens with.
        ///
        /// "Display the commands for getting started and the most commonly used subcommands first" - clig.dev. Six
        /// lines that answer what most people opened the terminal for, above a map of everything else. A list of
        /// seventy-five, however well grouped, answers nobody's question on the way past.
        /// </summary>
        public static readonly string[] Common =
        {
            "give", "teleport", "settime", "changecash", "spawnvehicle", "setweather",
        };

        /// <summary>Whether this word names a group, so `help world` can list one.</summary>
        public static bool IsTopic(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;

            foreach ((string topic, string[] _) in Groups)
                if (string.Equals(topic, name, StringComparison.OrdinalIgnoreCase)) return true;

            return string.Equals(name, Terminal, StringComparison.OrdinalIgnoreCase);
        }

        private static readonly Dictionary<string, string> ByWord = Index();

        /// <summary>The heading a command belongs under.</summary>
        public static string TopicOf(string word) =>
            word != null && ByWord.TryGetValue(word, out string topic) ? topic : Other;

        private static Dictionary<string, string> Index()
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach ((string topic, string[] words) in Groups)
                foreach (string word in words)
                    map[word] = topic;

            return map;
        }
    }
}
