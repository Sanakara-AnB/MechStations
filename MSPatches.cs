using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using RimWorld;
using Verse;
using Verse.AI;

namespace MechStations
{
    // Redirects vanilla's low-energy charger search to one of our stations.
    //
    // This runs INSIDE vanilla's energy branch, which sits high in the think
    // tree - above work. That is what protects a charging mech from being
    // pulled away, exactly as vanilla protects a mech at its own charger.
    // No extra logic is needed on our side for that.
    //
    // Only overrides when our station is genuinely closer than whatever
    // charger vanilla picked, so vanilla chargers keep working normally.
    [HarmonyPatch(typeof(JobGiver_GetEnergy_Charger), "TryGiveJob")]
    public static class Patch_JobGiver_GetEnergy_Charger
    {
        [HarmonyPostfix]
        public static void Postfix(ref Job __result, Pawn pawn)
        {
            if (pawn == null || pawn.Map == null) return;
            if (!pawn.IsColonyMechPlayerControlled) return;

            Need_MechEnergy energy = pawn.needs?.energy;
            if (energy == null) return;

            // Same threshold vanilla uses to decide "time to recharge".
            if (((Need)energy).CurLevel + 0.1f
                >= JobGiver_GetEnergy.GetMinAutorechargeThreshold(pawn))
                return;

            // A mech with its own station always goes home - no distance
            // comparison. The station is its base, and at the vanilla recharge
            // threshold it has roughly two days of runtime left to get there.
            MSCompMechDocking own = MSUtility.FindStationForMech(pawn);
            if (own != null && own.IsUsableBy(pawn))
            {
                __result = MSUtility.MakeDockingJob(pawn, own);
                return;
            }

            // Own station unusable (no power, other map, unreachable or taken)
            // or none assigned at all: behave exactly like an unassigned mech -
            // nearest free station versus vanilla charger, shortest wins. No
            // preference for our own buildings.
            MSCompMechDocking free = MSUtility.FindNearestFreeStation(pawn);
            if (free == null) return;

            float stationDist = pawn.Position.DistanceTo(free.DockingCell);
            float chargerDist = __result != null
                ? pawn.Position.DistanceTo(__result.targetA.Cell)
                : float.MaxValue;

            // Vanilla charger is closer or equal - leave its decision alone.
            if (stationDist >= chargerDist) return;

            __result = MSUtility.MakeDockingJob(pawn, free);
        }
    }

    // WARNING - GLOBAL PATCH.
    //
    // Pawn.GetGizmos runs for EVERY pawn, not just mechs: colonists, animals,
    // visitors, enemies. The postfix therefore answers the cheap questions
    // FIRST and leaves __result untouched unless this really is a mech with a
    // station - a colonist costs one type check and nothing else. Only a mech
    // that will actually get the gizmo pays for the wrapping iterator.
    //
    // Measured scope (verified against GizmoGridDrawer.DrawGizmoGridFor):
    // gizmos are rebuilt at most once per frame, and only for currently
    // selected things - there is a per-frame cache keyed on Time.frameCount
    // and the selection list. Nothing runs during ticking, pathfinding or any
    // other simulation work, and nothing is blocked or locked.
    //
    // Alternative if this ever becomes unwanted: the same action is already
    // available as a gizmo on the station itself (MSCompMechDocking), which
    // needs no patch at all. This one can simply be removed.
    [HarmonyPatch(typeof(Pawn), "GetGizmos")]
    [StaticConstructorOnStartup]
    public static class Patch_Pawn_GetGizmos
    {
        // Cached like every other icon in the mod: this postfix runs for every
        // selected pawn, so the lookup should not repeat per frame.
        private static Texture2D _iconReturn;
        private static Texture2D IconReturn =>
            _iconReturn ?? (_iconReturn = ContentFinder<Texture2D>.Get("UI/Commands/MS_Return"));

        [HarmonyPostfix]
        public static void Postfix(ref IEnumerable<Gizmo> __result, Pawn __instance)
        {
            if (__instance == null) return;
            if (!__instance.IsColonyMechPlayerControlled) return;

            MSCompMechDocking station = MSUtility.FindStationForMech(__instance);
            if (station == null) return;
            if (station.parent == null || !station.parent.Spawned) return;
            if (station.parent.Map != __instance.Map) return;

            __result = WithReturnGizmo(__result, station);
        }


        private static IEnumerable<Gizmo> WithReturnGizmo(
            IEnumerable<Gizmo> original, MSCompMechDocking station)
        {
            foreach (Gizmo g in original)
                yield return g;

            yield return new Command_Action
            {
                defaultLabel = "MS_GizmoReturnLabel".Translate(),
                defaultDesc = "MS_GizmoReturnDesc".Translate(),
                icon = IconReturn,
                action = station.RecallAssignedMech
            };
        }
    }
}
