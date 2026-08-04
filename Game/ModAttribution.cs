using System.Diagnostics;
using System.Reflection;
using MelonLoader;

namespace Hash.Game
{
    /// <summary>
    /// Which mod supplied a thing.
    ///
    /// This is what turns a wall of a thousand item ids into something a player can read: `brickpress` sitting next
    /// to `Litterally v1.1.0` answers "where did this come from and can I trust it" without leaving the game. It is
    /// also the only way to tell a mod's command apart from a vanilla one, since the game keeps them in the same
    /// list.
    ///
    /// The unit is the .NET assembly, because that is the finest thing a command object can be traced back to. The
    /// cache is cleared whenever the index rebuilds, so a mod loaded mid-session is not remembered as Unknown.
    /// </summary>
    internal static class ModAttribution
    {
        internal const string Vanilla = "Vanilla";

        private const string Unknown = "Unknown";

        /// <summary>Assemblies that ARE the game. Both spellings, because the IL2CPP build renames the namespace but
        /// not every assembly.</summary>
        private static readonly string[] GameAssemblies =
        {
            "Assembly-CSharp", "Assembly-CSharp-firstpass",
            "ScheduleOne.Core", "Il2CppScheduleOne.Core",
        };

        /// <summary>Frames to walk past when asking "who called this": the runtime, the loader, the interop layer and
        /// this mod itself are never the answer.</summary>
        private static readonly string[] Uninteresting =
        {
            "System", "mscorlib", "netstandard", "UnityEngine", "Unity.",
            "0Harmony", "MelonLoader", "Il2CppInterop", "Il2Cpp", "Hash",
        };

        private static readonly Dictionary<Assembly, string> _cache = new();

        internal static void Forget() => _cache.Clear();

        /// <summary>Whether this assembly is the game rather than a mod.</summary>
        internal static bool IsGame(Assembly assembly)
        {
            string name = assembly?.GetName().Name;
            if (string.IsNullOrEmpty(name)) return false;

            foreach (string game in GameAssemblies)
                if (string.Equals(name, game, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>The label for whoever owns this type - "Vanilla", or a mod's name and version.</summary>
        internal static string For(Type type)
        {
            if (type == null) return Unknown;

            return For(type.Assembly);
        }

        internal static string For(Assembly assembly)
        {
            if (assembly == null) return Unknown;
            if (_cache.TryGetValue(assembly, out string cached)) return cached;

            string label = Resolve(assembly);
            _cache[assembly] = label;
            return label;
        }

        private static string Resolve(Assembly assembly)
        {
            if (IsGame(assembly)) return Vanilla;

            // The melon list is the best answer: it carries the name and version the player sees everywhere else.
            try
            {
                foreach (MelonBase melon in MelonBase.RegisteredMelons)
                {
                    if (melon?.MelonAssembly?.Assembly != assembly) continue;

                    string name = melon.Info?.Name;
                    string version = melon.Info?.Version;

                    if (!string.IsNullOrEmpty(name))
                        return string.IsNullOrEmpty(version) ? name : $"{name} v{version}";
                }
            }
            catch { /* the melon list is a convenience; falling through is always safe */ }

            // A library that is not itself a melon still carries the attribute if it was built as one.
            try
            {
                var info = assembly.GetCustomAttribute<MelonInfoAttribute>();
                if (info != null && !string.IsNullOrEmpty(info.Name))
                    return string.IsNullOrEmpty(info.Version) ? info.Name : $"{info.Name} v{info.Version}";
            }
            catch { }

            string plain = assembly.GetName().Name;
            if (string.IsNullOrEmpty(plain)) return Unknown;

            // An interop wrapper is the game wearing a different name.
            if (plain.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase)
                || plain.StartsWith("Il2Cpp", StringComparison.OrdinalIgnoreCase))
                return Vanilla;

            return plain;
        }

        /// <summary>
        /// Who is calling, for things that carry no type of their own.
        ///
        /// Every item in the game is an <c>ItemDefinition</c>, so an item added by a mod is indistinguishable from a
        /// vanilla one by inspection - the only moment the answer exists is while the mod is registering it. This
        /// walks the stack at that moment and takes the first frame belonging to nobody else.
        ///
        /// Best effort by nature: IL2CPP stack traces are incomplete, and an inlined frame is simply not there. An
        /// item that cannot be traced is labelled as coming from a mod without saying which, which is still more
        /// than the game offers.
        /// </summary>
        internal static string FromStack(int skipFrames)
        {
            try
            {
                var trace = new StackTrace(skipFrames + 1, false);

                for (int i = 0; i < trace.FrameCount; i++)
                {
                    Type declaring = trace.GetFrame(i)?.GetMethod()?.DeclaringType;
                    if (declaring == null) continue;

                    string name = declaring.Assembly.GetName().Name;
                    if (string.IsNullOrEmpty(name) || IsUninteresting(name)) continue;
                    if (IsGame(declaring.Assembly)) continue;

                    return For(declaring.Assembly);
                }
            }
            catch { }

            return null;
        }

        private static bool IsUninteresting(string assemblyName)
        {
            foreach (string prefix in Uninteresting)
                if (assemblyName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }
    }
}
