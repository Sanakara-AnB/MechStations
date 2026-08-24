using System.Collections.Generic;
using System.Linq;
using Verse;

namespace MechStations
{
    // Treats one mech as a different weight class - but ONLY for our ports.
    //
    // Some mechs carry a weight class that does not match their footprint on
    // screen. Vanilla's Pikeman is Medium, yet draws wider than most Heavy
    // mechs and overlaps the modules of a 1x1 port.
    //
    // Nothing global is changed: RaceProps.mechWeightClass stays untouched, so
    // vanilla rechargers, bandwidth, overseer limits and every other mod still
    // see the original class. Only MSCompProperties_MechDocking.Accepts reads
    // the override, which is the single place all three decision paths go
    // through - job assignment, cell detection and the assign gizmo.
    //
    // A def rather than a hardcoded name, so other mods can add their own
    // oversized mechs without touching this assembly.
    public class MSMechClassOverrideDef : Def
    {
        public ThingDef mech;
        public MechWeightClassDef treatAs;

        // Set this to give the entry its own checkbox in the mod options,
        // labelled with the keyed string named here. Left empty, the entry
        // follows the single main switch - which is what the vanilla pikeman
        // does, since a player who turns the feature off means all of it.
        //
        // A per-mod entry needs its own box because the player may want the
        // fix for vanilla and not for a mod mech. Nothing here names a mod:
        // the entry only exists when its MayRequire let it load, so an
        // uninstalled mod produces no checkbox on its own.
        public string optionLabelKey;

        public bool HasOwnToggle => !optionLabelKey.NullOrEmpty();

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string e in base.ConfigErrors()) yield return e;

            if (mech == null)
                yield return "MSMechClassOverrideDef: mech is null.";
            if (treatAs == null)
                yield return "MSMechClassOverrideDef: treatAs is null.";
        }
    }

    public static class MSMechClassOverrides
    {
        // Def -> the override entry, NOT the replacement class directly. The
        // entry's checkbox can be flipped in the mod options at any time, so
        // baking its state into the cache would freeze whatever was set when
        // the cache was first built. Defs themselves are immutable and finite,
        // so the mapping cannot go stale during a session.
        // Cleared by MSGameComponent on a game switch, same discipline as the
        // station registries.
        private static Dictionary<ThingDef, MSMechClassOverrideDef> _map;

        public static void Clear() => _map = null;

        /// <summary>
        /// The weight class our ports should use for this mech: the override if
        /// one exists and the setting is on, otherwise the real one.
        /// </summary>
        public static MechWeightClassDef EffectiveClassFor(Pawn mech) =>
            EffectiveClassFor(mech?.def);

        /// <summary>
        /// Same answer from the race def alone. The startup compatibility check
        /// runs before any pawn exists, and the lookup never needed more than
        /// the def anyway.
        /// </summary>
        public static MechWeightClassDef EffectiveClassFor(ThingDef race)
        {
            if (race?.race == null) return null;
            MechWeightClassDef real = race.race.mechWeightClass;

            if (!MSMod.Settings.applyMechClassOverrides) return real;

            if (_map == null)
            {
                _map = new Dictionary<ThingDef, MSMechClassOverrideDef>();
                List<MSMechClassOverrideDef> all =
                    DefDatabase<MSMechClassOverrideDef>.AllDefsListForReading;
                for (int i = 0; i < all.Count; i++)
                {
                    MSMechClassOverrideDef d = all[i];
                    if (d.mech != null && d.treatAs != null)
                        _map[d.mech] = d;
                }
            }

            if (!_map.TryGetValue(race, out MSMechClassOverrideDef entry))
                return real;

            return MSMod.Settings.OverrideEnabled(entry) ? entry.treatAs : real;
        }

        /// <summary>
        /// One label key per checkbox to draw, in a stable order. Distinct,
        /// because several entries may share a key - a mod with two oversized
        /// mechs gets one box named after the mod rather than two identical
        /// captions.
        /// </summary>
        public static IEnumerable<string> ToggleLabelKeys() =>
            DefDatabase<MSMechClassOverrideDef>.AllDefsListForReading
                .Where(d => d.HasOwnToggle)
                .Select(d => d.optionLabelKey)
                .Distinct()
                .OrderBy(k => k);
    }
}
