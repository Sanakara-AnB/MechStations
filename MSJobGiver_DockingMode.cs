using Verse;
using Verse.AI;

namespace MechStations
{
    // Drives the whole MS_Docking work mode branch: go to a station and stay
    // on it, whether or not the mech has one assigned.
    //
    //   ThinkNode_ConditionalWorkMode [workMode=MS_Docking]
    //     JobGiver_SeekAllowedArea
    //     MSJobGiver_DockingMode              <- THIS
    //     JobGiver_GetEnergy_Charger          <- fallback: any vanilla charger
    //     JobGiver_GetEnergy_SelfShutdown     <- last resort
    //
    // Order of preference:
    //   1. already standing on a usable station -> hold it
    //   2. own assigned station                 -> go there
    //   3. nearest free station                 -> go there
    //   4. nothing                              -> fall through to the givers
    //                                              below, i.e. plain vanilla
    //                                              behaviour (top up at a
    //                                              charger, then go dormant)
    //
    // Step 1 is what makes this mode differ from vanilla Recharge: there, a
    // mech that reaches 100% steps off and shuts down beside the charger so the
    // next one gets a turn. That is right for shared chargers and wrong for
    // per-mech stations, so this mode holds the spot instead.
    //
    // No work mode check is needed here: the surrounding think node already
    // restricts this to MS_Docking, so every other mode is untouched.
    public class MSJobGiver_DockingMode : ThinkNode_JobGiver
    {
        protected override Job TryGiveJob(Pawn pawn)
        {
            if (!pawn.IsColonyMechPlayerControlled) return null;
            if (pawn.Map == null) return null;

            // 1) Already on a station that can serve it - stay put.
            //    Checked by cell rather than by assignment so an unassigned
            //    mech holds the free station it happens to be standing on.
            //    Someone else's assigned station does not count, so a mech that
            //    wandered onto one is not allowed to squat there.
            MSCompMechDocking here = MSUtility.FindStationAtCell(pawn.Position, pawn.Map);
            if (here != null
                && (here.assignedMech == null || here.assignedMech == pawn)
                && here.IsUsableBy(pawn))
                return MSUtility.MakeDockingJob(pawn, here);

            // 2) Own station first.
            MSCompMechDocking own = MSUtility.FindStationForMech(pawn);
            if (own != null && own.IsUsableBy(pawn))
                return MSUtility.MakeDockingJob(pawn, own);

            // 3) Otherwise any free one.
            MSCompMechDocking free = MSUtility.FindNearestFreeStation(pawn);
            if (free != null)
                return MSUtility.MakeDockingJob(pawn, free);

            // 4) Nothing available - let the vanilla givers below handle it.
            return null;
        }
    }
}
