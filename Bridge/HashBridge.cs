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
        /// Listing only: the declaring mod's prefix keeps running the line.</summary>
        public static readonly Action<string, string, string, string> Declare = DeclaredCommands.Declare;
    }
}
