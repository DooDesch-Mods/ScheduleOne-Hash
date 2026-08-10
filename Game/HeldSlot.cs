using HarmonyLib;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.PlayerScripts;

namespace Hash.Game
{
    /// <summary>
    /// Putting the hotbar slot back for as long as one command runs.
    ///
    /// <para><b>The phone empties your hand.</b> Not visually - the game genuinely unequips. The UI state the phone
    /// pushes carries <c>Equipping = Disabled</c>, which reaches <c>PlayerInventory.SetEquippingEnabled(false)</c>,
    /// and that stashes the slot into <c>PriorEquippedSlotIndex</c> and sets <c>EquippedSlotIndex</c> to -1. Every
    /// vanilla command that acts on what you are holding reads <c>equippedSlot</c>, so from a terminal that lives ON
    /// the phone all three of them answer for an empty hand: `packageproduct` says "No product equipped",
    /// `setquality` "No quality item equipped", `setquantity` "Nothing equipped". The hotbar still shows the item and
    /// `#hand` still names it, which is what makes the refusal read as a bug rather than as a state.</para>
    ///
    /// <para>So the index is put back for the length of the call and taken away again after. The raw field is written
    /// rather than the property, because the property mirrors to <c>Player.SetEquippedSlotIndex</c> and telling the
    /// server twice about an equip that never happened is noise nobody can act on. The slots themselves are never
    /// touched: nothing is selected, no viewmodel is built, and the item change replicates through the slot's own
    /// <c>onItemDataChanged</c>, which carries the index it was wired with at startup.</para>
    ///
    /// <para>Only when equipping is <b>disabled</b>, which is the same as saying "a UI took the hand away". A player
    /// who simply has an empty slot selected still gets the vanilla refusal, because there is nothing to put back and
    /// <c>PriorEquippedSlotIndex</c> would be naming something they put down a while ago.</para>
    /// </summary>
    internal sealed class HeldSlot : IDisposable
    {
        /// <summary>True while a command is running with the hand put back. Read by <see cref="EquipRefreshPatch"/>,
        /// and safe as a plain static: <c>SubmitCommand</c> is synchronous and the game is one thread.</summary>
        internal static bool Restoring { get; private set; }

        private readonly PlayerInventory _inventory;

        private HeldSlot(PlayerInventory inventory)
        {
            _inventory = inventory;
            Restoring = true;
        }

        /// <summary>
        /// The slot the player is holding, counting the one a UI has taken away. -1 when there is none.
        ///
        /// <c>PriorEquippedSlotIndex</c> only means "what was in the hand" while equipping is off; with equipping
        /// on it is a leftover from the last time a UI was open, and reading it then would name something the
        /// player put down a while ago.
        /// </summary>
        internal static int Index(PlayerInventory inventory)
        {
            if (inventory == null) return -1;
            if (inventory._equippedSlotIndex != -1) return inventory._equippedSlotIndex;

            return inventory.EquippingEnabled ? -1 : inventory.PriorEquippedSlotIndex;
        }

        /// <summary>
        /// The hand as the player left it, or null when there is nothing to restore.
        ///
        /// Null rather than a no-op object so the caller can `using` it either way - a null `using` does nothing,
        /// which is exactly the wanted behaviour when the game can already answer for itself.
        /// </summary>
        internal static HeldSlot Restored()
        {
            try
            {
                if (!PlayerSingleton<PlayerInventory>.InstanceExists) return null;

                PlayerInventory inventory = PlayerSingleton<PlayerInventory>.Instance;
                if (inventory == null) return null;

                // Something is equipped, or the hand is empty because the player emptied it. Both are the game's
                // own answer and neither is ours to improve on.
                if (inventory.EquippingEnabled || inventory._equippedSlotIndex != -1) return null;

                int prior = Index(inventory);
                if (prior < 0 || inventory.IndexAllSlots(prior) == null) return null;

                inventory._equippedSlotIndex = prior;
                return new HeldSlot(inventory);
            }
            catch (Exception e)
            {
                // A command that runs against an empty hand is a worse answer than one that runs at all, so this
                // never stops the line - it just leaves the game to say what it would have said.
                Core.Log?.Warning("the equipped slot could not be put back for this command: " + e.Message);
                return null;
            }
        }

        public void Dispose()
        {
            Restoring = false;

            try
            {
                // The refresh got through anyway - a small IL2CPP method can be inlined into its callers, and then
                // the prefix below never sees the call. Vanilla's own teardown is the only correct way back from
                // that: it deselects the slot, destroys the viewmodel it just built, and refills
                // PriorEquippedSlotIndex. Clearing the index by hand first would strand both.
                if (_inventory.EquippingEnabled) { _inventory.SetEquippingEnabled(false); return; }

                _inventory._equippedSlotIndex = -1;
            }
            catch (Exception e)
            {
                Core.Log?.Warning("the equipped slot could not be cleared again: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Holding vanilla's viewmodel refresh while the hand is on loan.
    ///
    /// `packageproduct` and `setquality` end with <c>SetEquippingEnabled(false)</c> then <c>(true)</c>, which is how
    /// they get the new jar to appear in the player's hand. Both calls are wrong while the phone is up: the first
    /// finds equipping already off and returns, and the second turns it back ON, re-equips the slot and puts the item
    /// in the player's hand - viewmodel, networked equip and all - while they are looking at their phone. There is
    /// nothing to refresh anyway; the hand is rebuilt from <c>PriorEquippedSlotIndex</c> the moment the phone goes
    /// down, which is the first time anyone could see it.
    ///
    /// <para>Suppressed for exactly the span of one restored command. Nothing else can be calling it in that window:
    /// the three call sites are the console command itself, the UI state transition and
    /// <c>SetInventoryEnabled</c>, and a synchronous <c>SubmitCommand</c> leaves no frame for either of the other
    /// two.</para>
    /// </summary>
    [HarmonyPatch(typeof(PlayerInventory), nameof(PlayerInventory.SetEquippingEnabled))]
    internal static class EquipRefreshPatch
    {
        [HarmonyPrefix]
        private static bool Prefix() => !HeldSlot.Restoring;
    }
}
