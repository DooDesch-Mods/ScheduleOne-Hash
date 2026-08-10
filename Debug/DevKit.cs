#if DEBUG
using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;
using UnityEngine;
using GameConsole = Il2CppScheduleOne.Console;

namespace Hash.Debugging
{
    /// <summary>
    /// Two words that exist only so the hand can be tested at all. Compiled out of Release.
    ///
    /// <para>Nothing in the game equips a hotbar slot except a key press, and a key press is the one thing the
    /// automation cannot make - which is why every bug about what the player is holding used to end at "try it
    /// yourself". <c>hashequip</c> is that key press as a command, and <c>hashhand</c> prints the four numbers that
    /// decide whether a command sees an empty hand.</para>
    ///
    /// <para>Both <c>SubmitCommand</c> overloads are patched because either one can be the caller - the string body
    /// calls the list body, so a single submission fires both prefixes, and the side effects are deduplicated per
    /// frame and signature so an equip never happens twice.</para>
    /// </summary>
    internal static class DevKit
    {
        private static readonly string[][] Listing =
        {
            new[] { "hashequip", "equip a hotbar slot, the way a number key would", "hashequip 0" },
            new[] { "hashhand", "what the game thinks is in the player's hand", "hashhand" },
        };

        internal static void Declare()
        {
            foreach (string[] one in Listing) Hash.Game.DeclaredCommands.Declare(one[0], one[1], one[2], "hash");
        }

        private static int _lastFrame = -1;
        private static string _lastSignature = "";

        /// <summary>True when the line was ours and the game should not see it.</summary>
        private static bool Dispatch(string[] parts)
        {
            if (parts.Length == 0) return false;

            string word = parts[0].ToLowerInvariant();
            if (word != "hashequip" && word != "hashhand") return false;

            string signature = string.Join(" ", parts);
            int frame = Time.frameCount;
            if (frame == _lastFrame && signature == _lastSignature) return true;

            _lastFrame = frame;
            _lastSignature = signature;

            try
            {
                if (word == "hashequip") Equip(parts.Length > 1 ? parts[1] : "0");
                else Report();
            }
            catch (Exception e)
            {
                Core.Log?.Error($"{word} failed: {e}");
            }

            return true;
        }

        private static PlayerInventory Inventory =>
            PlayerSingleton<PlayerInventory>.InstanceExists ? PlayerSingleton<PlayerInventory>.Instance : null;

        private static void Equip(string argument)
        {
            PlayerInventory inventory = Inventory;
            if (inventory == null) { Core.Log?.Warning("hashequip: no inventory - are you in a save?"); return; }

            if (!int.TryParse(argument, out int index) || index < 0)
            {
                Core.Log?.Warning("hashequip <slot>, 0-7. Slot 0 is the leftmost.");
                return;
            }

            HotbarSlot slot = inventory.IndexAllSlots(index);
            if (slot == null) { Core.Log?.Warning($"hashequip: there is no slot {index}."); return; }

            if (!inventory.EquippingEnabled)
            {
                Core.Log?.Warning("hashequip: equipping is off right now - put the phone away first.");
                return;
            }

            inventory.Equip(slot);
            Core.Log?.Msg($"hashequip: slot {index} -> {Name(slot)}");
        }

        private static void Report()
        {
            PlayerInventory inventory = Inventory;
            if (inventory == null) { Core.Log?.Warning("hashhand: no inventory - are you in a save?"); return; }

            int equipped = inventory._equippedSlotIndex;
            int prior = inventory.PriorEquippedSlotIndex;

            Core.Log?.Msg($"hashhand: equipping={inventory.EquippingEnabled} equipped={equipped} prior={prior} "
                          + $"restoring={Hash.Game.HeldSlot.Restoring}");
            Core.Log?.Msg($"hashhand: in hand = {Name(inventory.IndexAllSlots(equipped))}, "
                          + $"prior slot = {Name(inventory.IndexAllSlots(prior))}");
        }

        /// <summary>What a slot holds, spelled out enough to see a packaging or quality change land.</summary>
        private static string Name(HotbarSlot slot)
        {
            if (slot == null) return "(no slot)";

            Il2CppScheduleOne.ItemFramework.ItemInstance item = slot.ItemInstance;
            if (item == null) return "(empty)";

            string text = $"{item.Definition?.ID ?? "?"} x{item.Quantity}";

            var product = item.TryCast<Il2CppScheduleOne.Product.ProductItemInstance>();
            if (product != null) text += $" packaging={product.PackagingID ?? "(none)"} quality={product.Quality}";

            return text;
        }

        [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.SubmitCommand), new[] { typeof(string) })]
        private static class SubmitString
        {
            private static bool Prefix(string args)
            {
                if (string.IsNullOrWhiteSpace(args)) return true;

                return !Dispatch(args.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            }
        }

        [HarmonyPatch(typeof(GameConsole), nameof(GameConsole.SubmitCommand),
                      new[] { typeof(Il2CppSystem.Collections.Generic.List<string>) })]
        private static class SubmitList
        {
            private static bool Prefix(Il2CppSystem.Collections.Generic.List<string> args)
            {
                if (args == null || args.Count == 0) return true;

                var parts = new string[args.Count];
                for (int i = 0; i < args.Count; i++) parts[i] = args[i];

                return !Dispatch(parts);
            }
        }
    }
}
#endif
