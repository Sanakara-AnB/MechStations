using Verse;
using Verse.AI;

namespace MechStations
{
    // Sends an assigned mech back to its station when it has nothing to do.
    //
    // Inserted into the mechanoid think trees by ThinTree_Mechanoid.xml at two
    // points per tree: the idle block (so it beats aimless wandering) and the
    // combat-mech patrol block.
    //
    // This giver sits low in the tree, so work and combat outrank it. Whether
    // the mech then stays put is decided by MSUtility.MakeDockingJob: below the
    // recharge target it gets a charge session that cannot be interrupted,
    // otherwise a parking job that yields to work at once.
    public class MSJobGiver_DockingReturn : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!pawn.IsColonyMechPlayerControlled) return null;
            if (pawn.Map == null) return null;

            MSCompMechDocking station = MSUtility.FindStationForMech(pawn);
            if (station == null) return null;
            if (station.parent == null || !station.parent.Spawned) return null;
            if (station.parent.Map != pawn.Map) return null;

            // Do not travel to a station that cannot charge. A mech that is
            // ALREADY standing there keeps standing (it just gets a parking
            // job), which is what makes a power outage leave it in place
            // instead of sending it off.
            //
            // Deliberately BEFORE the reachability check below: that one is a
            // pathfinding query and the most expensive line in the mod
            // (measured at 7 to 299us per call, with spikes to 2ms). Running it
            // for a station the mech may not travel to anyway is pure waste -
            // the same cheapest-first ordering the ring scan already uses.
            if (!station.IsPowered && pawn.Position != station.DockingCell)
                return null;

            if (!pawn.CanReserveAndReach(station.DockingCell, PathEndMode.OnCell, Danger.Deadly))
                return null;

            return MSUtility.MakeDockingJob(pawn, station);
        }
    }
}
