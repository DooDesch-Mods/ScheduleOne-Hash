using Hash.Terminal;
using Il2CppScheduleOne;
using GameConsole = Il2CppScheduleOne.Console;

namespace Hash.Game
{
    /// <summary>
    /// Every console command the game currently knows.
    ///
    /// Read straight off <c>Console.Commands</c>, which is the one list both vanilla and mods register into - so a
    /// mod's command is indexed for free, and nothing has to be told about it. The catch is the other direction: a
    /// command that only exists inside a mod's own dispatcher, or that goes through S1API instead of the native
    /// list, is invisible here because it is invisible to the game too.
    ///
    /// Rebuilt rather than watched. The list only changes when a mod registers something, which happens during load
    /// and, for a few mods, on the first scene - so rebuilding when the terminal opens costs nothing and is always
    /// right, where an event subscription would need the game to raise one.
    /// </summary>
    internal sealed class CommandIndex : ICommandCatalogue
    {
        private readonly ArgProviders _providers;

        private List<CommandInfo> _commands = new();
        private bool _dirty = true;

        internal CommandIndex(ArgProviders providers) => _providers = providers;

        public IReadOnlyList<CommandInfo> Commands
        {
            get
            {
                if (_dirty) Rebuild();
                return _commands;
            }
        }

        public IReadOnlyList<ArgValue> ValuesFor(string command, int argIndex) =>
            _providers.ValuesFor(command, argIndex);

        public bool Owns(string command, int argIndex) => _providers.Owns(command, argIndex);

        internal void MarkDirty() => _dirty = true;

        private void Rebuild()
        {
            _dirty = false;
            ModAttribution.Forget();

            var found = new List<CommandInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Il2CppSystem.Collections.Generic.List<GameConsole.ConsoleCommand> commands = GameConsole.Commands;
                if (commands == null)
                {
                    Core.Log?.Warning("[hash] the game has no command list yet - the terminal will be empty.");
                    _commands = found;
                    return;
                }

                for (int i = 0; i < commands.Count; i++)
                {
                    GameConsole.ConsoleCommand command = commands[i];
                    if (command == null) continue;

                    CommandInfo info = Describe(command);
                    if (info == null || info.Word.Length == 0) continue;

                    // First registration wins. A duplicate word means one of the two can never be reached anyway,
                    // and it is the game's dictionary lookup that decides which - not this list's order.
                    if (!seen.Add(info.Word)) continue;

                    found.Add(info);
                }
            }
            catch (Exception e)
            {
                Core.Log?.Error("[hash] reading the command list failed: " + e);
            }

            found.Sort((a, b) => string.Compare(a.Word, b.Word, StringComparison.OrdinalIgnoreCase));
            _commands = found;

            Core.Log?.Msg($"[hash] indexed {found.Count} command(s).");
        }

        private static CommandInfo Describe(GameConsole.ConsoleCommand command)
        {
            try
            {
                string word = (command.CommandWord ?? "").Trim().ToLowerInvariant();
                if (word.Length == 0) return null;

                string description = (command.CommandDescription ?? "").Trim();
                string usage = (command.ExampleUsage ?? "").Trim();

                Type type = command.GetType();
                bool vanilla = IsVanilla(type);

                return new CommandInfo(
                    word, description, usage,
                    UsageExample.Signature(word, usage),
                    vanilla ? ModAttribution.Vanilla : ModAttribution.For(type),
                    vanilla);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[hash] a command refused to describe itself: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Whether this command is the game's own.
        ///
        /// Three tests rather than one, because each catches a case the others miss: vanilla commands are nested
        /// inside <c>Console</c>, they live in the game's namespace, and they come from the game's assembly. A mod
        /// that puts its command in a namespace starting with the game's would be mislabelled, which is a price
        /// worth paying for correctly labelling every command that actually exists.
        /// </summary>
        private static bool IsVanilla(Type type)
        {
            if (type == null) return false;
            if (type.DeclaringType == typeof(GameConsole)) return true;

            string ns = type.Namespace ?? "";
            if (ns.StartsWith("Il2CppScheduleOne", StringComparison.Ordinal)
                || ns.StartsWith("ScheduleOne", StringComparison.Ordinal)) return true;

            return ModAttribution.IsGame(type.Assembly);
        }
    }
}
