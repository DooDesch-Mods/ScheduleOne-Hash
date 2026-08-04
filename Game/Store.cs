using Hash.Terminal;
using Il2CppScheduleOne.Persistence;
using Il2CppScheduleOne.DevUtilities;
using MelonLoader.Utils;

namespace Hash.Game
{
    /// <summary>
    /// Where the terminal keeps its files.
    ///
    /// Two scopes, because the two kinds of state answer different questions. Usage counts describe THIS save, so
    /// they sit beside it and go when it goes. History and aliases describe the player: starting a new game with an
    /// empty alias table reads as having lost them, so those live once per machine.
    ///
    /// Nothing here ever throws. A terminal that cannot open because a text file has a bad byte in it would be worse
    /// than one that forgot what you typed yesterday.
    /// </summary>
    internal sealed class Store : IStore
    {
        private const string Folder = "Hash";

        public string Read(StoreScope scope, string name)
        {
            try
            {
                string path = PathFor(scope, name);
                return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[hash] could not read {name}: {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Write, via a temporary file.
        ///
        /// The rename is atomic, so a crash mid-write costs the new content rather than the old: a half-written
        /// history is worse than yesterday's history, and the game does crash.
        /// </summary>
        public void Write(StoreScope scope, string name, string content)
        {
            try
            {
                string path = PathFor(scope, name);
                if (path == null) return;

                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                string temp = path + ".tmp";
                File.WriteAllText(temp, content ?? "");

                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
            }
            catch (Exception e)
            {
                Core.Log?.Warning($"[hash] could not write {name}: {e.Message}");
            }
        }

        private static string PathFor(StoreScope scope, string name)
        {
            if (string.IsNullOrEmpty(name)) return null;

            if (scope == StoreScope.Global)
                return Path.Combine(MelonEnvironment.UserDataDirectory, Folder, name);

            string save = SaveFolder();
            return save == null ? null : Path.Combine(save, "Modded", Folder, name);
        }

        /// <summary>
        /// The folder of the save that is loaded, or null on the main menu.
        ///
        /// Null is a normal answer, not a failure: the terminal opens before a save does, and per-save counts simply
        /// have nowhere to go until one is loaded.
        /// </summary>
        private static string SaveFolder()
        {
            try
            {
                LoadManager manager = Singleton<LoadManager>.InstanceExists ? Singleton<LoadManager>.Instance : null;
                string path = manager?.LoadedGameFolderPath;

                return string.IsNullOrEmpty(path) ? null : path;
            }
            catch { return null; }
        }
    }
}
