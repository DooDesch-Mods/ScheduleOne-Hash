using Hash.Terminal;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.Interaction;
using Il2CppScheduleOne.NPCs;
using Il2CppScheduleOne.PlayerScripts;
using Il2CppScheduleOne.Property;
using Il2CppScheduleOne.Vehicles;
using UnityEngine;

namespace Hash.Game
{
    /// <summary>
    /// Where `#` gets its answer: the thing the player was looking at, and the rest of the family.
    ///
    /// <para><b>Why this has to run every frame.</b> The obvious design is to look once, when the terminal opens.
    /// That cannot work: <c>InteractionManager.CheckHover</c> starts with
    /// <c>if (IsAnythingBlockingInteraction()) { HoveredInteractableObject = null; }</c>, and the phone being up is
    /// one of the things that blocks. By the time anyone could ask, the game has already forgotten. So the target is
    /// sampled continuously and the last good one is kept with a timestamp.</para>
    ///
    /// <para><b>Why the ray is our own.</b> Vanilla's hover reaches four metres and only sees objects carrying an
    /// <c>InteractableObject</c> - NPCs, vehicles, doors, stations. Not buildings: looking at the barn from the road
    /// returns nothing at all. The cast here goes further and falls back to the property containing the hit point,
    /// which is what makes `setowned #` work from outside.</para>
    ///
    /// <para>Everything here is local and read-only. The interaction stack is a plain Singleton with no RPC, so a
    /// client can resolve a mark perfectly well - it just cannot run the command afterwards.</para>
    /// </summary>
    internal sealed class WorldMarks : IMarks
    {
        /// <summary>How long a target stays valid after it leaves the reticle.
        ///
        /// Long enough to glance away and press the key, short enough that a mark can never be about a room you
        /// left. Three seconds is roughly "the thing I was just looking at" and nothing more.</summary>
        private const float Remembered = 3f;

        /// <summary>How far the cast reaches. Vanilla stops at four metres because it is about reaching things; this
        /// is about naming them, and a building has to be nameable from the pavement.</summary>
        private const float Range = 30f;

        /// <summary>The sphere the cast sweeps, copied from vanilla's own hover. A bare ray needs pixel-accurate aim
        /// at anything thin; a small sphere is what makes pointing at a person feel like pointing at a person.</summary>
        private const float Radius = 0.2f;

        /// <summary>
        /// Seconds between casts. Ten a second, not sixty.
        ///
        /// Measured: at every frame this was 0.085 ms/frame, which made hash the second most expensive mod in the
        /// game while doing nothing anybody asked for - the terminal may never be opened at all. Nothing here needs
        /// frame accuracy: the mark answers "what was I looking at", and a tenth of a second is far below the time
        /// it takes to look at something and reach for a key.
        /// </summary>
        private const float Interval = 0.1f;

        private Mark _last = Mark.None;
        private float _lastAt = float.NegativeInfinity;
        private float _nextCast;

        /// <summary>The collider the last cast hit, and what it resolved to. Walking a hierarchy to find an NPC and
        /// then testing every property's bounds is the expensive half, and standing still means hitting the same
        /// wall over and over.</summary>
        private int _lastCollider;
        private Mark _lastResolved = Mark.None;

        /// <summary>Every word as it stood the moment the phone came up.</summary>
        private readonly Dictionary<string, Mark> _frozen = new(StringComparer.Ordinal);

        private bool _terminalOpen;

        /// <summary>
        /// One frame of looking, and the moment everything freezes.
        ///
        /// <b>Crossing into the terminal freezes every word for as long as the phone is out.</b> That is the whole
        /// contract: a mark names the world as it was when the player chose to open the terminal, and nothing they
        /// do afterwards - reading, typing, walking around with the phone in hand - can change what they were
        /// pointing at.
        ///
        /// Without it every word rots differently while the line is being written. The NPC you marked walks away and
        /// `#` expires. You step out of the shop and `#here` becomes the street. Somebody wanders past and `#near`
        /// is them. Each one is a command doing something the player did not ask for, for a reason invisible from
        /// the line they typed.
        /// </summary>
        internal void Tick()
        {
            if (Sideload.Api.PhoneScreen.IsRaised)
            {
                if (!_terminalOpen) { Freeze(); _terminalOpen = true; }

                return;
            }

            _terminalOpen = false;

            if (Time.unscaledTime < _nextCast) return;
            _nextCast = Time.unscaledTime + Interval;

            Mark seen = Cast();
            if (!seen.Exists) return;

            _last = seen;
            _lastAt = Time.unscaledTime;
        }

        private void Freeze()
        {
            _frozen.Clear();
            _frozen["#"] = Recent();
            _frozen["#hand"] = Live(() => Hand);
            _frozen["#here"] = Live(() => Here);
            _frozen["#car"] = Live(() => Car);
            _frozen["#home"] = Live(() => Home);
            _frozen["#near"] = Live(() => Near);
        }

        /// <summary>The frozen answer while the terminal is up, the live one otherwise.</summary>
        private Mark Held(string word, Func<Mark> live) =>
            _terminalOpen && _frozen.TryGetValue(word, out Mark held) ? held : live();

        public Mark Looked => Held("#", Recent);

        /// <summary>The last target, if it was seen recently enough to still be the thing the player means.</summary>
        private Mark Recent() => Time.unscaledTime - _lastAt <= Remembered ? _last : Mark.None;

        private Mark LiveHand
        {
            get
            {
                try
                {
                    PlayerInventory inventory = PlayerSingleton<PlayerInventory>.InstanceExists
                        ? PlayerSingleton<PlayerInventory>.Instance
                        : null;

                    string id = inventory?.equippedSlot?.ItemInstance?.Definition?.ID;

                    return string.IsNullOrEmpty(id)
                        ? Mark.None
                        : new Mark(MarkKind.Item, id.ToLowerInvariant(), id);
                }
                catch (Exception e) { return Failed("#hand", e); }
            }
        }

        /// <summary>
        /// The property the player is standing in.
        ///
        /// Not <c>Player.CurrentProperty</c>: that tests only the property whose bounding box CENTRE is nearest,
        /// so standing in a small property next to a large one reports nothing. The same bounds test over every
        /// property costs nothing at this rate and is right every time.
        /// </summary>
        private Mark LiveHere
        {
            get
            {
                try
                {
                    Player player = Player.Local;
                    if (player?.Avatar == null) return Mark.None;

                    return PropertyAt(player.Avatar.CenterPoint);
                }
                catch (Exception e) { return Failed("#here", e); }
            }
        }

        private Mark LiveCar
        {
            get
            {
                try
                {
                    LandVehicle vehicle = Player.Local?.CurrentVehicle?.GetComponent<LandVehicle>();
                    string code = vehicle?.VehicleCode;

                    return string.IsNullOrEmpty(code)
                        ? Mark.None
                        : new Mark(MarkKind.Vehicle, code.ToLowerInvariant(), code);
                }
                catch (Exception e) { return Failed("#car", e); }
            }
        }

        private Mark LiveHome
        {
            get
            {
                try
                {
                    Property property = Player.Local?.LastVisitedProperty;
                    if (property == null || !property.IsOwned) return Mark.None;

                    return Of(property);
                }
                catch (Exception e) { return Failed("#home", e); }
            }
        }

        private Mark LiveNear
        {
            get
            {
                try
                {
                    Player player = Player.Local;
                    if (player == null) return Mark.None;

                    Vector3 from = player.transform.position;
                    Il2CppSystem.Collections.Generic.List<NPC> all = NPCManager.NPCRegistry;
                    if (all == null) return Mark.None;

                    NPC closest = null;
                    float best = float.MaxValue;

                    for (int i = 0; i < all.Count; i++)
                    {
                        NPC npc = all[i];
                        if (npc == null || string.IsNullOrWhiteSpace(npc.ID)) continue;

                        float distance = (npc.transform.position - from).sqrMagnitude;
                        if (distance >= best) continue;

                        best = distance;
                        closest = npc;
                    }

                    return closest == null
                        ? Mark.None
                        : new Mark(MarkKind.Npc, closest.ID.ToLowerInvariant(), closest.FullName);
                }
                catch (Exception e) { return Failed("#near", e); }
            }
        }

        // Each word: frozen while the terminal is up, read live otherwise.
        public Mark Hand => Held("#hand", () => LiveHand);

        public Mark Here => Held("#here", () => LiveHere);

        public Mark Car => Held("#car", () => LiveCar);

        public Mark Home => Held("#home", () => LiveHome);

        public Mark Near => Held("#near", () => LiveNear);

        /// <summary>Read one now, for freezing. Wrapped so a reader that throws costs its own word and not the
        /// snapshot.</summary>
        private static Mark Live(Func<Mark> read)
        {
            try { return read(); } catch { return Mark.None; }
        }

        // ------------------------------------------------------------------------------------------ looking --

        /// <summary>
        /// One cast, resolved to the most specific thing it hit.
        ///
        /// Order matters and is not arbitrary: a person standing inside a property is the person. Anything else
        /// would make `#` unusable indoors, where most of this game happens.
        /// </summary>
        private Mark Cast()
        {
            try
            {
                PlayerCamera camera = PlayerSingleton<PlayerCamera>.InstanceExists
                    ? PlayerSingleton<PlayerCamera>.Instance
                    : null;

                if (camera == null) return Mark.None;

                // Read the mask off the game rather than naming layers. Vanilla does the same in five equippables,
                // and a hardcoded layer index is wrong the first time the project's layers are reordered.
                LayerMask mask = Singleton<InteractionManager>.InstanceExists
                    ? Singleton<InteractionManager>.Instance.Interaction_SearchMask
                    : ~0;

                if (!camera.LookRaycast(Range, out RaycastHit hit, mask, includeTriggers: true, radius: Radius))
                    return Mark.None;

                if (hit.collider == null) return Mark.None;

                // Same collider as last time means the same answer. Standing in a room looking at one wall is the
                // normal case, and it is the case that would otherwise walk a hierarchy and test fifteen bounding
                // boxes ten times a second for a result that cannot have changed.
                int collider = hit.collider.GetInstanceID();
                if (collider == _lastCollider) return _lastResolved;

                _lastCollider = collider;
                _lastResolved = Resolve(hit);

                return _lastResolved;
            }
            catch (Exception e) { return Failed("#", e); }
        }

        /// <summary>What one hit names, most specific first: a person standing inside a shop is the person.</summary>
        private static Mark Resolve(RaycastHit hit)
        {
            NPC npc = hit.collider.GetComponentInParent<NPC>();
            if (npc != null && !string.IsNullOrWhiteSpace(npc.ID))
                return new Mark(MarkKind.Npc, npc.ID.ToLowerInvariant(), npc.FullName);

            LandVehicle vehicle = hit.collider.GetComponentInParent<LandVehicle>();
            if (vehicle != null && !string.IsNullOrWhiteSpace(vehicle.VehicleCode))
                return new Mark(MarkKind.Vehicle, vehicle.VehicleCode.ToLowerInvariant(), vehicle.VehicleCode);

            return PropertyAt(hit.point);
        }

        private static Mark PropertyAt(Vector3 point)
        {
            Il2CppSystem.Collections.Generic.List<Property> all = Property.Properties;
            if (all == null) return Mark.None;

            for (int i = 0; i < all.Count; i++)
            {
                Property property = all[i];
                if (property == null || !property.DoBoundsContainPoint(point)) continue;

                return Of(property);
            }

            return Mark.None;
        }

        private static Mark Of(Property property)
        {
            string code = property.PropertyCode;

            return string.IsNullOrWhiteSpace(code)
                ? Mark.None
                : new Mark(MarkKind.Property, code.ToLowerInvariant(), property.PropertyName);
        }

        /// <summary>A mark that could not be read is no mark. Logged once per kind rather than per frame, because
        /// the cast runs sixty times a second and a broken one would drown the log in a minute.</summary>
        private static readonly HashSet<string> _complained = new();

        private static Mark Failed(string word, Exception e)
        {
            if (_complained.Add(word)) Core.Log?.Warning($"[hash] {word} could not be read: {e.Message}");

            return Mark.None;
        }
    }
}
