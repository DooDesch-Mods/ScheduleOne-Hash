using System;
using System.Collections.Generic;
using System.Reflection;

namespace Hash.Api
{
    /// <summary>
    /// Tell the game's command list about a command your mod answers to itself.
    ///
    /// A mod that handles console input with a Harmony prefix on <c>SubmitCommand</c> - the pattern for anything
    /// that wants subcommands or arguments the game's own <c>ConsoleCommand</c> cannot express - registers nothing.
    /// Its words run when typed and are invisible everywhere else: the in-game command list, an autocomplete, a help
    /// overlay, hash. There is nothing to enumerate, because the words only exist as string literals in a switch.
    ///
    /// One call fixes that:
    /// <code>
    ///   HashCommands.Add("snitch", "profiler: start, stop, top, report", "snitch start");
    /// </code>
    ///
    /// <para>Listing only. Your prefix keeps running the command exactly as it does now, and the entry this creates
    /// never dispatches - a second route would risk running the same line twice.</para>
    ///
    /// <para>Reach it through <c>using Hash.Api;</c> and call <c>HashCommands.Add(...)</c>. Writing
    /// <c>Hash.Api.HashCommands</c> out in full inside a <c>MelonMod</c> does not compile: <c>MelonBase</c> has a
    /// string property called <c>Hash</c>, and the member wins over the namespace.</para>
    ///
    /// <para>One file, no references, no hard dependency. Compile it into your mod
    /// (<c>&lt;Compile Include="path\to\HashCommands.cs" /&gt;</c>). Every call is a no-op while hash is absent, and
    /// calls made before it loads are replayed once it appears, so load order does not matter.</para>
    /// </summary>
    public static class HashCommands
    {
        private const string BridgeTypeName = "Hash.Bridge.HashBridge, Hash";

        private static readonly List<string[]> _pending = new List<string[]>();

        private static Action<string, string, string> _declare;
        private static bool _bound;

        /// <summary>True once hash is installed and listening. You rarely need this - <see cref="Add"/> is safe
        /// either way; use it to decide whether to log something to the player instead.</summary>
        public static bool Available { get { Bind(); return _bound; } }

        /// <summary>
        /// Declare a command word your mod answers to.
        ///
        /// The word goes into the game's own command list, which is what every tool reads - so this reaches the
        /// vanilla list and any autocomplete as well, not only hash.
        /// </summary>
        /// <param name="word">The first token of the line, without arguments. Case does not matter.</param>
        /// <param name="description">One line, lowercase, no trailing period - the way the game words its own.</param>
        /// <param name="example">A line that would work, e.g. `snitch top 10`. Optional but worth it: hash shows it
        /// as the argument shape while the player is typing.</param>
        public static void Add(string word, string description, string example = null)
        {
            if (string.IsNullOrEmpty(word)) return;

            Bind();

            if (_declare != null)
            {
                Safely(word, description, example);
                return;
            }

            _pending.Add(new[] { word, description ?? "", example ?? "" });
        }

        private static void Safely(string word, string description, string example)
        {
            // A shim never takes the caller down. Anything wrong on the host side costs this one word.
            try { _declare(word, description ?? "", example ?? ""); }
            catch { }
        }

        private static void Bind()
        {
            if (_bound) return;

            // Looked up on every call until it works, because hash may load after the mod that declares. Once found,
            // the pending calls go over and the lookup never runs again.
            try
            {
                Type bridge = Type.GetType(BridgeTypeName, false);
                if (bridge == null) return;

                FieldInfo field = bridge.GetField("Declare", BindingFlags.Public | BindingFlags.Static);
                _declare = field?.GetValue(null) as Action<string, string, string>;
                if (_declare == null) return;

                _bound = true;

                foreach (string[] one in _pending) Safely(one[0], one[1], one[2]);
                _pending.Clear();
            }
            catch
            {
                // Reflection failing here means an incompatible hash. Staying silent is right: the mod's commands
                // still run, they are just not listed.
            }
        }
    }
}
