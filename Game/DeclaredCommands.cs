using Il2CppInterop.Runtime.Injection;
using MelonLoader;
using GameConsole = Il2CppScheduleOne.Console;

namespace Hash.Game
{
    /// <summary>
    /// Commands a mod answers to with its own <c>SubmitCommand</c> prefix, put into the game's command list so the
    /// rest of the world can see them.
    ///
    /// That prefix pattern is how a mod gets subcommands and arguments the game's own <c>ConsoleCommand</c> cannot
    /// express, and it is what our own tooling uses. The cost is that nothing is registered: `snitch` runs when
    /// typed, and no list, autocomplete or help overlay can learn it exists. A mod calls
    /// <c>HashCommands.Add(...)</c>, this puts an entry in <c>Console.Commands</c>, and everything reading that
    /// list - hash included, since its index is built from it - shows the word beside the vanilla ones.
    ///
    /// <para>Only the public <c>Commands</c> list is touched. The private <c>commands</c> dictionary is what vanilla
    /// dispatch looks in, and staying out of it is deliberate: the declaring mod's prefix already handles the line,
    /// and a second route would risk running it twice.</para>
    ///
    /// <para>Injected here rather than in the shim on purpose. The shim is compiled into every mod that uses it, and
    /// an Il2Cpp type is registered by full name - two mods carrying the same injected class would collide in the
    /// domain. One type, in one assembly, cannot.</para>
    /// </summary>
    internal static class DeclaredCommands
    {
        private static readonly List<Declaration> _declared = new();

        /// <summary>What a mod asked for, whether or not it is in the game's list yet.</summary>
        private sealed class Declaration
        {
            internal Declaration(string word, string description, string example, string owner)
            {
                Word = word;
                Description = description;
                Example = example;
                Owner = owner;
            }

            internal string Word { get; }

            internal string Description { get; }

            internal string Example { get; }

            /// <summary>The assembly that declared it, so the terminal files the word under that mod and not
            /// under hash - the entry's own type lives here, which is what attribution would otherwise find.</summary>
            internal string Owner { get; }
        }

        /// <summary>Called by a mod through <c>Hash.Api.HashCommands</c>. Safe before the console exists.</summary>
        internal static void Declare(string word, string description, string example, string owner)
        {
            if (string.IsNullOrWhiteSpace(word)) return;

            word = word.Trim();

            foreach (Declaration known in _declared)
            {
                if (string.Equals(known.Word, word, StringComparison.OrdinalIgnoreCase)) return;
            }

            _declared.Add(new Declaration(word, Clean(description), Clean(example), Clean(owner)));
            Core.Log?.Msg($"[hash] '{word}' declared by {(string.IsNullOrEmpty(owner) ? "a mod" : owner)}"
                          + " - it will be listed with the game's own.");

            Apply();
        }

        /// <summary>
        /// Put every declaration the game does not have yet into its list.
        ///
        /// Run again after the console awakes: <c>Console.Awake</c> fills its stores only when they are empty, so a
        /// scene change either keeps what is there or starts over, and both cases are covered by checking first.
        /// </summary>
        internal static void Apply()
        {
            if (_declared.Count == 0) return;

            try
            {
                Il2CppSystem.Collections.Generic.List<GameConsole.ConsoleCommand> list = GameConsole.Commands;
                if (list == null) return;   // no console yet; the awake hook comes back to this

                foreach (Declaration declaration in _declared)
                {
                    if (Has(list, declaration.Word)) continue;

                    DeclaredCommand entry = new();
                    entry.Configure(declaration.Word, declaration.Description, declaration.Example);
                    list.Add(entry);
                }
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[hash] a declared command could not be listed: " + e.Message);
            }
        }

        /// <summary>Which mod declared this word, or "" when nobody did.</summary>
        internal static string OwnerOf(string word)
        {
            foreach (Declaration declaration in _declared)
            {
                if (string.Equals(declaration.Word, word, StringComparison.OrdinalIgnoreCase)) return declaration.Owner;
            }
            return "";
        }

        private static bool Has(Il2CppSystem.Collections.Generic.List<GameConsole.ConsoleCommand> list, string word)
        {
            for (int i = 0; i < list.Count; i++)
            {
                try
                {
                    if (list[i] != null && string.Equals(list[i].CommandWord, word, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }

        private static string Clean(string value)
        {
            string one = (value ?? "").Replace("\r", "").Replace('\n', ' ').Trim();
            return one.Length > 200 ? one.Substring(0, 197) + "..." : one;
        }
    }

    /// <summary>
    /// The entry in the game's list. Word, description and example live in managed fields, so one injected type
    /// backs every declaration rather than one type per mod.
    /// </summary>
    [RegisterTypeInIl2Cpp]
    internal sealed class DeclaredCommand : GameConsole.ConsoleCommand
    {
        private string _word = string.Empty;
        private string _description = string.Empty;
        private string _example = string.Empty;

        public DeclaredCommand(IntPtr ptr) : base(ptr) { }

        public DeclaredCommand() : base(ClassInjector.DerivedConstructorPointer<DeclaredCommand>())
        {
            ClassInjector.DerivedConstructorBody(this);
        }

        internal void Configure(string word, string description, string example)
        {
            _word = word;
            _description = description;
            _example = example;
        }

        public override string CommandWord => _word;

        public override string CommandDescription => _description;

        public override string ExampleUsage => _example;

        /// <summary>
        /// Deliberately empty. The mod that declared this word handles it in its own prefix, and vanilla dispatch
        /// never reaches here anyway - this entry is in the public list, not in the dictionary dispatch reads.
        /// </summary>
        public override void Execute(Il2CppSystem.Collections.Generic.List<string> args) { }
    }
}
