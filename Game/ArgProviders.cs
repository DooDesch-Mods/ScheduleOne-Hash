using Hash.Terminal;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Product.Packaging;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;
using GameConsole = Il2CppScheduleOne.Console;
using GameRegistry = Il2CppScheduleOne.Registry;

namespace Hash.Game
{
    /// <summary>
    /// What can go in each argument of each command.
    ///
    /// The game says nothing about this - a <c>ConsoleCommand</c> takes a list of strings and figures it out itself -
    /// so every entry in the table below is knowledge about the game written down once. That is also why the table is
    /// short and flat rather than a class per command: the interesting part is the mapping, and twelve near-identical
    /// classes around twelve one-line lookups is how the previous version got to six hundred lines.
    ///
    /// A slot that is claimed but empty is not the same as an unclaimed one. "There are no vehicles loaded yet" must
    /// show nothing; "nobody wrote a provider for this" should fall back to mining the command's own usage example.
    /// <see cref="Owns"/> is the difference.
    /// </summary>
    internal sealed class ArgProviders
    {
        /// <summary>Values are re-read from the game on demand but not more than this often. A thousand item ids
        /// walked on every keystroke is wasted work; a second is short enough that a mod adding an item mid-session
        /// still shows up before the player notices.</summary>
        private const double CacheSeconds = 1.0;

        private readonly Dictionary<string, Func<IEnumerable<ArgValue>>> _slots =
            new(StringComparer.OrdinalIgnoreCase);

        private readonly Dictionary<string, (float At, IReadOnlyList<ArgValue> Values)> _cache =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Which mod added which item, filled in by the registry patch as items are added. An item nobody
        /// claimed is the game's.</summary>
        private readonly Dictionary<string, string> _itemSource = new(StringComparer.OrdinalIgnoreCase);

        internal ArgProviders()
        {
            // ---- items -------------------------------------------------------------------------------------
            Slot("give", 0, () => Items(null));
            Slot("setdiscovered", 0, () => Items(item => item.TryCast<ProductDefinition>() != null));
            Slot("packageproduct", 0, () => Packaging());

            // ---- places ------------------------------------------------------------------------------------
            Slot("teleport", 0, TeleportTargets);
            Slot("setowned", 0, () => Properties());

            // ---- people ------------------------------------------------------------------------------------
            Slot("setunlocked", 0, Npcs);
            Slot("setrelationship", 0, Npcs);

            // ---- things ------------------------------------------------------------------------------------
            Slot("spawnvehicle", 0, Vehicles);

            // ---- enumerations ------------------------------------------------------------------------------
            Slot("setquality", 0, () => EnumNames<EQuality>());
            Slot("setregionunlocked", 0, () => EnumNames<EMapRegion>());
            Slot("addemployee", 0, () => EnumNames<EEmployeeType>());
            Slot("addemployee", 1, () => Properties());

            // ---- fixed choices the game does not spell out anywhere -----------------------------------------
            Slot("setpoliceignoreplayers", 0, () => Literals("true", "false"));
            Slot("setweather", 0, () => Literals("clear", "lightrain", "heavyrain"));
        }

        /// <summary>Note that a mod added this item, so its rows can say which one.</summary>
        internal void RememberItemSource(string id, string source)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(source)) return;

            _itemSource[id] = source;
        }

        internal bool Owns(string command, int argIndex) => _slots.ContainsKey(Key(command, argIndex));

        internal IReadOnlyList<ArgValue> ValuesFor(string command, int argIndex)
        {
            string key = Key(command, argIndex);
            if (!_slots.TryGetValue(key, out Func<IEnumerable<ArgValue>> source)) return Array.Empty<ArgValue>();

            float now = Time.unscaledTime;
            if (_cache.TryGetValue(key, out (float At, IReadOnlyList<ArgValue> Values) hit)
                && now - hit.At < CacheSeconds)
                return hit.Values;

            IReadOnlyList<ArgValue> values = Collect(source);
            _cache[key] = (now, values);
            return values;
        }

        internal void Invalidate() => _cache.Clear();

        /// <summary>
        /// Run a provider and hand back what it produced.
        ///
        /// Wrapped because every one of these reaches into a game singleton that may not exist yet: the terminal can
        /// be opened on the main menu, during a load, or before a manager has spawned. A provider that throws costs
        /// its own list and nothing else.
        /// </summary>
        private static IReadOnlyList<ArgValue> Collect(Func<IEnumerable<ArgValue>> source)
        {
            var values = new List<ArgValue>();

            try
            {
                foreach (ArgValue value in source())
                    if (!string.IsNullOrWhiteSpace(value.Value)) values.Add(value);
            }
            catch (Exception e)
            {
                Core.Log?.Warning("[hash] an argument provider failed: " + e.Message);
            }

            return values;
        }

        private void Slot(string command, int argIndex, Func<IEnumerable<ArgValue>> source) =>
            _slots[Key(command, argIndex)] = source;

        private static string Key(string command, int argIndex) => command + "#" + argIndex;

        // ------------------------------------------------------------------------------------------- sources --

        /// <summary>
        /// Every item in the registry, optionally filtered.
        ///
        /// Cash is left out on purpose: `give cash 100` is not how money works in this game, and offering it as the
        /// first alphabetical hit sends people down a dead end.
        /// </summary>
        private IEnumerable<ArgValue> Items(Func<ItemDefinition, bool> keep)
        {
            GameRegistry registry = Singleton<GameRegistry>.InstanceExists ? Singleton<GameRegistry>.Instance : null;
            if (registry == null) yield break;

            Il2CppSystem.Collections.Generic.List<ItemDefinition> all = registry.GetAllItems();
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                ItemDefinition item = all[i];
                if (item == null) continue;

                string id = item.ID;
                if (string.IsNullOrWhiteSpace(id)) continue;
                if (string.Equals(id, "cash", StringComparison.OrdinalIgnoreCase)) continue;
                if (keep != null && !keep(item)) continue;

                yield return Item(id);
            }
        }

        /// <summary>
        /// The packaging items.
        ///
        /// Filtered by type like the others, with one guard the others do not need: under IL2CPP the cast can come
        /// back empty for a type the game clearly has, and an empty packaging list makes `packageproduct`
        /// uncompletable. Falling back to the two the game ships is better than offering nothing.
        /// </summary>
        private IEnumerable<ArgValue> Packaging()
        {
            var found = new List<ArgValue>();

            foreach (ArgValue value in Items(item => item.TryCast<PackagingDefinition>() != null)) found.Add(value);

            if (found.Count > 0) return found;

            return new[] { Item("jar"), Item("baggie") };
        }

        private ArgValue Item(string id) =>
            _itemSource.TryGetValue(id, out string mod)
                ? new ArgValue(id.ToLowerInvariant(), mod, false)
                : new ArgValue(id.ToLowerInvariant(), ModAttribution.Vanilla, true);

        /// <summary>
        /// Everywhere `teleport` accepts: the named points, then every property, then every NPC.
        ///
        /// Three sources in one slot because the command takes all three and the player does not care which kind of
        /// thing they are naming - they want to go to the docks, or to Ray, and the terminal should not make them
        /// know that one is a transform and the other is a person.
        /// </summary>
        private static IEnumerable<ArgValue> TeleportTargets()
        {
            GameConsole console = Singleton<GameConsole>.InstanceExists ? Singleton<GameConsole>.Instance : null;
            Transform points = console?.TeleportPointsContainer;

            if (points != null)
            {
                for (int i = 0; i < points.childCount; i++)
                {
                    Transform child = points.GetChild(i);
                    if (child != null) yield return new ArgValue(child.name.ToLowerInvariant(), ModAttribution.Vanilla, true);
                }
            }

            foreach (ArgValue property in Properties()) yield return property;
            foreach (ArgValue npc in Npcs()) yield return npc;
        }

        private static IEnumerable<ArgValue> Properties()
        {
            Il2CppSystem.Collections.Generic.List<Property> all = Property.Properties;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                Property property = all[i];
                if (property == null) continue;

                string code = property.PropertyCode;
                if (!string.IsNullOrWhiteSpace(code))
                    yield return new ArgValue(code.ToLowerInvariant(), ModAttribution.Vanilla, true);
            }
        }

        private static IEnumerable<ArgValue> Npcs()
        {
            Il2CppSystem.Collections.Generic.List<NPC> all = NPCManager.NPCRegistry;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                NPC npc = all[i];
                if (npc == null) continue;

                string id = npc.ID;
                if (string.IsNullOrWhiteSpace(id)) continue;

                // An NPC added by a mod is worth naming: a list where half the people are someone else's is the
                // case attribution exists for.
                string source = ModAttribution.For(npc.GetType());
                yield return new ArgValue(id.ToLowerInvariant(), source, source == ModAttribution.Vanilla);
            }
        }

        private static IEnumerable<ArgValue> Vehicles()
        {
            VehicleManager manager = NetworkSingleton<VehicleManager>.InstanceExists
                ? NetworkSingleton<VehicleManager>.Instance
                : null;

            Il2CppSystem.Collections.Generic.List<LandVehicle> all = manager?.VehiclePrefabs;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                LandVehicle vehicle = all[i];
                if (vehicle == null) continue;

                string code = vehicle.VehicleCode;
                if (!string.IsNullOrWhiteSpace(code))
                    yield return new ArgValue(code.ToLowerInvariant(), ModAttribution.Vanilla, true);
            }
        }

        private static IEnumerable<ArgValue> EnumNames<T>() where T : struct, Enum
        {
            foreach (string name in Enum.GetNames(typeof(T)))
                yield return new ArgValue(name.ToLowerInvariant(), ModAttribution.Vanilla, true);
        }

        private static IEnumerable<ArgValue> Literals(params string[] values)
        {
            foreach (string value in values) yield return new ArgValue(value, ModAttribution.Vanilla, true);
        }
    }
}
