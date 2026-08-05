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
    /// <para>Two routes, best first. <c>ConsoleHelper.RegisteredCommands</c> is public API since S1API 3.1.9 and is
    /// what this reads. Older versions only have the internal <c>CustomConsoleRegistry</c>, which is still tried
    /// afterwards - it works, but a rename there would cost these commands silently, which is exactly why the
    /// public one was added.</para>
    /// </summary>
    internal static class S1ApiCommands
    {
        /// <summary>Public since S1API 3.1.9 (ifBars/S1API#200) - the supported way to enumerate.</summary>
        private const string HelperTypeName = "S1API.Console.ConsoleHelper";

        /// <summary>Internal, and all an older S1API has.</summary>
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

        /// <summary>The RegisteredCommands property on one named type, or null when this assembly is not it.</summary>
        private static PropertyInfo ListingOn(Assembly assembly, string typeName)
        {
            try
            {
                Type type = assembly.GetType(typeName, false);
                if (type == null) return null;

                PropertyInfo property = type.GetProperty("RegisteredCommands",
                                                         BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (property == null)
                    WarnOnce($"{typeName} is here but has no RegisteredCommands - "
                             + "commands registered through S1API will not be listed.");

                return property;
            }
            catch { return null; }
        }

        private static string Read(object target, string property)
        {
            try { return target.GetType().GetProperty(property)?.GetValue(target) as string; }
            catch { return null; }
        }

        /// <summary>
        /// Locate the listing once per index rebuild. Absent is the normal answer and says nothing - plenty of
        /// installs have no S1API at all.
        /// </summary>
        private static PropertyInfo Registry()
        {
            if (_looked) return _registered;
            _looked = true;

            try
            {
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    PropertyInfo found = ListingOn(assembly, HelperTypeName)
                                         ?? ListingOn(assembly, RegistryTypeName);
                    if (found == null) continue;

                    _registered = found;
                    Core.Log?.Msg($"[hash] reading S1API's commands from {found.DeclaringType?.FullName}.");
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
