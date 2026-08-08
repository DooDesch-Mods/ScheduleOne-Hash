using HarmonyLib;
using Il2CppScheduleOne.UI;

namespace Hash.Game
{
    /// <summary>
    /// Taking the console key.
    ///
    /// The player presses the key they have always pressed and the terminal comes up instead of the grey bar. That
    /// is done by refusing the vanilla panel rather than by reading the key ourselves: <c>ConsoleUI.Update</c> polls
    /// its own <c>InputActionReference</c>, which the player may have rebound, and the only way to honour a rebind
    /// without reimplementing it is to let the game decide the key and intercept what it does next.
    ///
    /// <para>So the prefix sits on <c>SetIsOpen</c> and answers three questions in order: is the hijack switched on,
    /// is this an open rather than a close, and can we actually put something on screen instead. If any answer is no,
    /// vanilla runs untouched - which is what makes the preference a real escape hatch and not a half-measure.</para>
    /// </summary>
    [HarmonyPatch(typeof(ConsoleUI), nameof(ConsoleUI.SetIsOpen))]
    internal static class ConsoleKeyPatch
    {
        /// <summary>Set by Core once the app is registered and the host is known to be new enough.</summary>
        internal static Func<bool> OnOpen;

        /// <summary>Off means the vanilla bar comes back, for a player who prefers it or a mod that needs it.</summary>
        internal static bool Enabled = true;

        [HarmonyPrefix]
        private static bool Prefix(bool open)
        {
            if (!Enabled || !open || OnOpen == null) return true;

            try
            {
                // The terminal took it. Returning false means the vanilla panel never opens, never grabs the
                // EventSystem, and never fights the phone for focus.
                if (OnOpen()) return false;
            }
            catch (Exception e)
            {
                Core.Log?.Error("opening the terminal failed, falling back to the vanilla console: " + e);
            }

            return true;
        }
    }

    /// <summary>
    /// Which mod added which item.
    ///
    /// There is no way to ask an <c>ItemDefinition</c> where it came from - every item in the game is one, vanilla
    /// or not. The only moment the answer exists is while the mod is registering it, so that is where it is taken.
    /// </summary>
    [HarmonyPatch(typeof(Il2CppScheduleOne.Registry), nameof(Il2CppScheduleOne.Registry.AddToRegistry))]
    internal static class ItemSourcePatch
    {
        internal static ArgProviders Providers;

        [HarmonyPostfix]
        private static void Postfix(Il2CppScheduleOne.ItemFramework.ItemDefinition item)
        {
            if (Providers == null || item == null) return;

            try
            {
                string id = item.ID;
                if (string.IsNullOrWhiteSpace(id)) return;

                // Two frames up is the caller: this postfix, then AddToRegistry itself.
                string source = ModAttribution.FromStack(2);
                if (source == null) return;

                Providers.RememberItemSource(id, source);
            }
            catch { /* attribution is a nicety; never let it cost the game an item */ }
        }
    }

    /// <summary>The command list changes when a mod registers into it, which happens around Console.Awake.</summary>
    [HarmonyPatch(typeof(Il2CppScheduleOne.Console), nameof(Il2CppScheduleOne.Console.Awake))]
    internal static class ConsoleAwakePatch
    {
        internal static Action OnAwake;

        [HarmonyPostfix]
        private static void Postfix()
        {
            try { OnAwake?.Invoke(); }
            catch (Exception e) { Core.Log?.Warning("rebuilding the command index failed: " + e.Message); }
        }
    }
}
