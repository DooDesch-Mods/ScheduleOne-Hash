using Hash.Game;

namespace Hash.Bridge
{
    /// <summary>
    /// The host side of the reflection handshake with <c>Hash.Api.HashCommands</c>. The shim finds this type by name
    /// and reads the static delegate fields; every signature uses only BCL types, so the two assemblies share no
    /// type and the shim stays a no-op when hash is not installed.
    ///
    /// Adding a field is backwards compatible - an older shim ignores it. Changing an existing signature is not.
    /// </summary>
    public static class HashBridge
    {
        /// <summary>word, description, example, the declaring assembly's name. Puts a mod's own command word
        /// into the game's command list, so every tool that reads it can show the word beside the vanilla ones.
        /// Listing only: the declaring mod's prefix keeps running the line.
        ///
        /// <para>Null until hash runs as a mod (<see cref="Open"/>). Finding this type is not enough: a launcher
        /// that holds hash back (the Side Hustle mod gate) still leaves Hash.dll where <c>Type.GetType</c> resolves
        /// it, and an entry made then is an Il2Cpp type MelonLoader never registered. The shim reads null as "not
        /// installed" and keeps its declarations pending.</para></summary>
        public static Action<string, string, string, string> Declare;

        /// <summary>Called from hash's own early init, before any mod's <c>OnInitializeMelon</c> declares.</summary>
        internal static void Open() => Declare = DeclaredCommands.Declare;
    }
}
