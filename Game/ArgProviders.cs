using Hash.Terminal;
using Il2CppScheduleOne.AvatarFramework.Emotions;
using Il2CppScheduleOne.Cutscenes;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Employees;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.Map;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Product;
using Il2CppScheduleOne.Product.Packaging;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Quests;
using Il2CppScheduleOne.Variables;
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

        private readonly Dictionary<string, MarkKind> _kinds = new(StringComparer.OrdinalIgnoreCase);

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

            // ---- quests ------------------------------------------------------------------------------------
            Slot("setqueststate", 0, Quests);
            Slot("setqueststate", 1, () => EnumNames<EQuestState>());
            Slot("setquestentrystate", 0, Quests);
            Slot("setquestentrystate", 2, () => EnumNames<EQuestState>());

            // ---- the rest of the game's own vocabularies -----------------------------------------------------
            Slot("setvar", 0, Variables);
            Slot("setemotion", 0, Emotions);
            Slot("playcutscene", 0, Cutscenes);

            // Two commands nobody could use without this. The labels live on a list in the scene and are written
            // nowhere else - not in the description, not in the example - so `disable` and `enable` shipped with
            // exactly one known argument: the `pp` in their own example.
            Slot("disable", 0, LabelledObjects);
            Slot("enable", 0, LabelledObjects);

            // ---- what a context word may stand in for --------------------------------------------------------
            //
            // Every argument that names a thing rather than a number. `#` is checked against this before it is
            // substituted, so an argument missing here simply refuses marks - which is the right way round.
            Kind("give", 0, MarkKind.Item);
            Kind("setdiscovered", 0, MarkKind.Item);
            Kind("packageproduct", 0, MarkKind.Item);

            Kind("setunlocked", 0, MarkKind.Npc);
            Kind("setrelationship", 0, MarkKind.Npc);

            Kind("spawnvehicle", 0, MarkKind.Vehicle);

            Kind("setowned", 0, MarkKind.Property);
            Kind("addemployee", 1, MarkKind.Property);

            // teleport takes all three, and a mark of any of them is right - the command sorts it out itself.
            Kind("teleport", 0, MarkKind.Any);

            // ---- fixed choices the game does not spell out anywhere -----------------------------------------
            Slot("setpoliceignoreplayers", 0, () => Literals("true", "false"));
            Slot("setweather", 0, () => Literals("clear", "lightrain", "heavyrain"));
            Slot("setrelationship", 1, () => Literals("0", "1", "2", "3", "4", "5"));
            Slot("bind", 0, () => EnumNames<KeyCode>());
            Slot("unbind", 0, () => EnumNames<KeyCode>());
        }

        /// <summary>Note that a mod added this item, so its rows can say which one.</summary>
        internal void RememberItemSource(string id, string source)
        {
            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(source)) return;

            _itemSource[id] = source;
        }

        internal bool Owns(string command, int argIndex) => _slots.ContainsKey(Key(command, argIndex));

        /// <summary>
        /// What kind of thing an argument names, for the context words to be checked against.
        ///
        /// Written next to the slot that fills it, in <see cref="Kind"/>, so the two cannot drift: the same table
        /// that knows `setrelationship` takes NPCs knows that `#` may stand there. An argument with no entry takes
        /// no mark at all, which is the safe default - a number or a free string is not something `#` can be.
        /// </summary>
        internal MarkKind KindOf(string command, int argIndex) =>
            _kinds.TryGetValue(Key(command, argIndex), out MarkKind kind) ? kind : MarkKind.None;

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

        /// <summary>Say what kind of thing this argument names. Only for slots a mark could stand in.</summary>
        private void Kind(string command, int argIndex, MarkKind kind) =>
            _kinds[Key(command, argIndex)] = kind;

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

        /// <summary>
        /// Every quest, by the title the command matches on.
        ///
        /// Spaces become underscores because that is what the command expects - it undoes the substitution before it
        /// looks the quest up, since a console line is split on spaces and a title like "Making a name" would arrive
        /// as three arguments.
        /// </summary>
        private static IEnumerable<ArgValue> Quests()
        {
            Il2CppSystem.Collections.Generic.List<Quest> all = Quest.Quests;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                Quest quest = all[i];
                if (quest == null) continue;

                string title = quest.Title;
                if (!string.IsNullOrWhiteSpace(title))
                    yield return new ArgValue(title.Replace(' ', '_').ToLowerInvariant(), ModAttribution.Vanilla, true);
            }
        }

        private static IEnumerable<ArgValue> Variables()
        {
            VariableDatabase database = NetworkSingleton<VariableDatabase>.InstanceExists
                ? NetworkSingleton<VariableDatabase>.Instance
                : null;

            Il2CppSystem.Collections.Generic.List<BaseVariable> all = database?.VariableList;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                BaseVariable variable = all[i];
                if (variable == null || string.IsNullOrWhiteSpace(variable.Name)) continue;

                yield return new ArgValue(variable.Name.ToLowerInvariant(), ModAttribution.Vanilla, true);
            }
        }

        /// <summary>The faces the player's own avatar knows. Read off the local player rather than a global list -
        /// that is where the command looks them up too, and there is no list anywhere else.</summary>
        private static IEnumerable<ArgValue> Emotions()
        {
            AvatarEmotionManager emotions = Player.Local?.Avatar?.EmotionManager;

            Il2CppSystem.Collections.Generic.List<AvatarEmotionPreset> all = emotions?.EmotionPresetList;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                AvatarEmotionPreset preset = all[i];
                if (preset == null || string.IsNullOrWhiteSpace(preset.PresetName)) continue;

                yield return new ArgValue(preset.PresetName.ToLowerInvariant(), ModAttribution.Vanilla, true);
            }
        }

        private static IEnumerable<ArgValue> Cutscenes()
        {
            CutsceneManager manager = Singleton<CutsceneManager>.InstanceExists
                ? Singleton<CutsceneManager>.Instance
                : null;

            Il2CppSystem.Collections.Generic.List<Cutscene> all = manager?.Cutscenes;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                Cutscene cutscene = all[i];
                if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.Name)) continue;

                yield return new ArgValue(cutscene.Name.ToLowerInvariant(), ModAttribution.Vanilla, true);
            }
        }

        /// <summary>The named GameObjects `disable` and `enable` accept.</summary>
        private static IEnumerable<ArgValue> LabelledObjects()
        {
            GameConsole console = Singleton<GameConsole>.InstanceExists ? Singleton<GameConsole>.Instance : null;

            Il2CppSystem.Collections.Generic.List<GameConsole.LabelledGameObject> all = console?.LabelledGameObjectList;
            if (all == null) yield break;

            for (int i = 0; i < all.Count; i++)
            {
                GameConsole.LabelledGameObject labelled = all[i];
                if (labelled == null || string.IsNullOrWhiteSpace(labelled.Label)) continue;

                yield return new ArgValue(labelled.Label.ToLowerInvariant(), ModAttribution.Vanilla, true);
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
