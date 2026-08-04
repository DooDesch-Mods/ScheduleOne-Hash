using System.Collections;
using System.Reflection;
using Hash.Terminal;

namespace Hash.Game
{
    /// <summary>
    /// Console commands registered through S1API rather than with the game.
    ///
    /// S1API keeps its own dictionary and routes to it from a prefix on <c>Console.SubmitCommand</c>, deliberately -
    /// its commands never inherit the game's abstract type, so the same source works on Mono and IL2CPP. The
    /// consequence is that they are invisible in <c>Console.Commands</c>: they RUN fine when typed, and nothing that
    /// reads the game's list can tell you they exist.
    ///
    /// That is why Snitch's commands never showed up in the old autocomplete, and why Yoink had to register itself
    /// into the native list a second time just to be discoverable. Reading the S1API registry directly retires that
    /// workaround: a mod registers once, the normal way, and the terminal finds it.
    ///
    /// <para>By reflection, because hash must not require S1API. The registry is internal to S1API, which rules out
    /// referencing it even if the dependency were acceptable - and a missing or renamed member has to cost these
    /// commands, not the whole index.</para>
    ///
    /// <para>Internal is the part worth replacing: a rename upstream costs these commands silently. S1API 3.1.8 has
    /// no public way to enumerate them; once <c>ConsoleHelper.RegisteredCommands</c> exists, read that instead and
    /// keep this path only for older versions.</para>
    /// </summary>
    internal static class S1ApiCommands
    {
        private const string RegistryTypeName = "S1API.Console.CustomConsoleRegistry";

        private static bool _looked;
        private static PropertyInfo _registered;
        private static bool _warned;

        /// <summary>Forget what was found, so a rebuild after S1API loaded picks it up.</summary>
        internal static void Forget()
        {
            _looked = false;
            _registered = null;
        }

        /// <summary>
        /// Every S1API command, or nothing at all when S1API is absent.
        ///
        /// <paramref name="known"/> is what the native list already produced: a mod that registers both ways - the
        /// workaround this replaces - would otherwise appear twice.
        /// </summary>
        internal static IEnumerable<CommandInfo> All(HashSet<string> known)
        {
            PropertyInfo property = Registry();
            if (property == null) yield break;

            IEnumerable entries;
            try
            {
                entries = property.GetValue(null) as IEnumerable;
            }
            catch (Exception e)
            {
                WarnOnce("could not read the S1API command registry: " + e.Message);
                yield break;
            }

            if (entries == null) yield break;

            foreach (object entry in entries)
            {
                CommandInfo info = Describe(entry);
                if (info == null || info.Word.Length == 0) continue;
                if (known != null && known.Contains(info.Word)) continue;

                yield return info;
            }
        }

        /// <summary>
        /// Read one dictionary entry.
        ///
        /// The entry is a KeyValuePair whose value is a BaseConsoleCommand - a public abstract with three public
        /// string properties. Everything is read by name so a change on S1API's side degrades to one missing command
        /// rather than an exception in the middle of building the index.
        /// </summary>
        private static CommandInfo Describe(object entry)
        {
            try
            {
                object command = entry?.GetType().GetProperty("Value")?.GetValue(entry);
                if (command == null) return null;

                string word = (Read(command, "CommandWord") ?? "").Trim().ToLowerInvariant();
                if (word.Length == 0) return null;

                string description = (Read(command, "CommandDescription") ?? "").Trim();
                string usage = (Read(command, "ExampleUsage") ?? "").Trim();

                return new CommandInfo(word, description, usage,
                                       UsageExample.Signature(word, usage),
                                       ModAttribution.For(command.GetType()),
                                       isVanilla: false);
            }
            catch (Exception e)
            {
                WarnOnce("an S1API command refused to describe itself: " + e.Message);
                return null;
            }
        }

        private static string Read(object target, string property)
        {
            try { return target.GetType().GetProperty(property)?.GetValue(target) as string; }
            catch { return null; }
        }

        /// <summary>Locate S1API's registry once per index rebuild. Absent is the normal answer and says nothing.</summary>
        private static PropertyInfo Registry()
        {
            if (_looked) return _registered;
            _looked = true;

            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    Type type;
                    try { type = assembly.GetType(RegistryTypeName, false); }
                    catch { continue; }

                    if (type == null) continue;

                    _registered = type.GetProperty("RegisteredCommands",
                                                   BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                    if (_registered == null)
                        WarnOnce("S1API is here but its command registry has no RegisteredCommands - "
                                 + "commands registered through it will not be listed.");

                    return _registered;
                }
            }
            catch (Exception e)
            {
                WarnOnce("looking for S1API failed: " + e.Message);
            }

            return null;
        }

        private static void WarnOnce(string message)
        {
            if (_warned) return;

            _warned = true;
            Core.Log?.Warning("[hash] " + message);
        }
    }
}
