using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MechStations
{
    // Placement rules for station modules.
    public class MSPlaceWorker_ModuleAlignment : PlaceWorker
    {
        // Modules are built from the gizmo on their port, never from the build
        // menu - they are useless anywhere else.
        //
        // This is the ONLY correct way to hide them. Dropping
        // designationCategory looks equivalent but is not: BuildableByPlayer
        // is defined as "designationCategory != null", and
        // ThingDefGenerator_Buildings only creates blueprint and frame defs for
        // buildable defs. Without a blueprint, Designator_Build.DesignateSingleCell
        // throws a NullReferenceException in GenSpawn.WipeExistingThings the
        // moment a player who is NOT in god mode clicks - god mode takes the
        // direct-spawn branch and hides the problem.
        public override bool IsBuildDesignatorVisible(BuildableDef def) => false;

        // Marks the cell the module is looking at. Without it the rotation is
        // guesswork: once a per-station limit is reached the ghost stays red
        // even when the module IS aligned correctly, so its colour cannot be
        // used to tell. An interaction cell would show the same thing but is
        // not usable here - it must stay clear of buildings, and it points at
        // the port, which is PassThroughOnly.
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot,
            Color ghostCol, Thing thing = null)
        {
            Map map = Find.CurrentMap;
            if (map == null) return;

            IntVec3 facing = center + rot.FacingCell;
            if (!facing.InBounds(map)) return;

            GenDraw.DrawTargetHighlight(facing);
        }

        public override AcceptanceReport AllowsPlacing(BuildableDef checkingDef,
            IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            if (!(checkingDef is ThingDef def)) return true;

            IntVec3 facing = loc + rot.FacingCell;
            if (!facing.InBounds(map))
                return "MS_Place_NoTargetPort".Translate();

            Building station = FindStationAt(facing, map, thingToIgnore);
            if (station == null)
                return "MS_Place_NoTargetPort".Translate();

            // The station lists which module defs it accepts, so mech modules
            // cannot be used on automatroid ports and vice versa. Our own
            // list on the docking props - the facility system is gone.
            MSCompProperties_MechDocking docking =
                station.def.GetCompProperties<MSCompProperties_MechDocking>();
            if (docking == null
                || docking.attachableModules == null
                || !docking.attachableModules.Contains(def))
                return "MS_Place_WrongPortType".Translate();

            // Flush alignment: centres must line up on the axis the module
            // faces along. Without this a 3x1 module could sit one cell off and
            // still link.
            bool aligned = rot.IsHorizontal
                ? loc.z == station.Position.z
                : loc.x == station.Position.x;
            if (!aligned)
                return "MS_Place_NotAligned".Translate();

            // Per-port limit, enforced HERE and only here - there is no link
            // system left to fall back on. Blueprints and frames are counted
            // too, so two cannot be queued at once.
            int? maxPerStation = MaxPerStationOf(def);
            if (maxPerStation.HasValue
                && CountAttached(def, station, map, thingToIgnore) >= maxPerStation.Value)
                return "MS_Place_TooManyModules".Translate(maxPerStation.Value);

            return true;
        }

        // How many things of this def (built, blueprint or frame) already face
        // the given station. Facing-cell membership is the same criterion the
        // link itself uses, so this count can never disagree with linking.
        private static int CountAttached(ThingDef def, Building station, Map map,
            Thing thingToIgnore)
        {
            int count = 0;
            count += CountFacing(map.listerThings.ThingsOfDef(def), station, thingToIgnore);
            if (def.blueprintDef != null)
                count += CountFacing(map.listerThings.ThingsOfDef(def.blueprintDef), station, thingToIgnore);
            if (def.frameDef != null)
                count += CountFacing(map.listerThings.ThingsOfDef(def.frameDef), station, thingToIgnore);
            return count;
        }

        // Uses the SAME facing test as everything at runtime
        // (MSUtility.ModuleFacesStation), so build permission and effect can
        // never disagree: whatever may be built here is exactly what the port
        // will read back.
        private static int CountFacing(List<Thing> things, Building station,
            Thing thingToIgnore)
        {
            int count = 0;
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];
                if (t == thingToIgnore) continue;
                if (MSUtility.ModuleFacesStation(t, station)) count++;
            }
            return count;
        }

        // The per-port limit from the module def's props, or null when the def
        // carries none. Nullable rather than 0, because the two must stay
        // distinguishable: a def without module props has no limit to enforce
        // here, whereas maxPerStation = 0 in XML means none may be built at
        // all - one absent value, one meaning.
        private static int? MaxPerStationOf(ThingDef def)
        {
            if (def.comps == null) return null;
            for (int i = 0; i < def.comps.Count; i++)
            {
                if (def.comps[i] is IMSAttachableModuleProps p)
                    return p.MaxPerStation;
            }
            return null;
        }

        private static Building FindStationAt(IntVec3 cell, Map map, Thing thingToIgnore)
        {
            // Blueprints and frames are ignored on purpose: a module may only
            // be placed against a station that actually exists.
            Building b = MSUtility.StationBuildingAt(cell, map);
            return (b == null || b == thingToIgnore) ? null : b;
        }
    }
}
