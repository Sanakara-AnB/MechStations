using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace MechStations
{
    // Serves BOTH station jobs. Which one is running is decided in
    // MSUtility.MakeDockingJob and read here from job.def.
    //
    //   MS_DockingCharge - charge session. No expiry, so the think tree is
    //                      never consulted and work cannot pull the mech off.
    //                      Ends when the mechanitor's upper recharge threshold
    //                      is reached, or if the station is gone/unpowered.
    //
    //   MS_DockingWait   - parking. Short expiry, work takes over immediately.
    //                      No charge-based end condition, so the mech simply
    //                      stays until it is needed.
    public class MSJobDriver_DockingWait : JobDriver
    {
        // How often the recharge TARGET is re-evaluated. The abort conditions
        // below stay on every tick - only this one question is throttled, and
        // 15 is the slowest rate vanilla's own tick interval ever falls to.
        private const int RechargeCheckInterval = 15;

        private bool IsChargeSession => job.def == MSJobDefOf.MS_DockingCharge;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            // Reserve the docking cell so two mechs cannot claim the same station.
            return pawn.Reserve(job.targetA, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoCell(TargetIndex.A, PathEndMode.OnCell);

            Toil wait = ToilMaker.MakeToil("MS_DockingWait");

            wait.initAction = () =>
            {
                // Looked up by cell, not by assignment: a mech sent to a FREE
                // station has no assignment at all.
                MSCompMechDocking station = MSUtility.FindStationAtCell(job.targetA.Cell, pawn.Map);
                if (station?.parent != null)
                {
                    // Defaults to the station's own rotation; the facing gizmo
                    // overrides it per station and the choice is saved, so it
                    // holds across the mech leaving and returning.
                    pawn.Rotation = station.MechFacingRotation;
                }
            };

            wait.defaultCompleteMode = ToilCompleteMode.Never;

            // Keeps the toil system from resetting the rotation set above.
            wait.handlingFacing = true;

            wait.AddEndCondition(() =>
            {
                MSCompMechDocking station = MSUtility.FindStationAtCell(job.targetA.Cell, pawn.Map);
                if (station == null) return JobCondition.Incompletable;

                if (IsChargeSession)
                {
                    // Mirrors vanilla's JobDriver_MechCharge, which fails on
                    // !CanPawnChargeCurrently - and that check covers BOTH power
                    // and a full waste container (verified via decompile).
                    //
                    // Without the waste half, a station whose container is full
                    // would stop charging while the job kept waiting for a
                    // target it can never reach: the mech would stand there
                    // indefinitely, unable to work or fight, because a charge
                    // session has no expiry and never re-consults the think
                    // tree. Failing out instead lets it go find power elsewhere.
                    if (!station.IsPowered) return JobCondition.Incompletable;
                    if (station.IsFullOfWaste) return JobCondition.Incompletable;

                    Need_MechEnergy energy = pawn.needs?.energy;
                    if (energy == null) return JobCondition.Incompletable;

                    // Throttled, unlike the aborts above: GetMaxRechargeLimit
                    // resolves the overseer relation and then searches every
                    // control group for this mech (verified via decompile).
                    // Vanilla's own charge driver asks it from
                    // tickIntervalAction - the adaptive 1-15 tick raster - not
                    // once per tick. Ending a quarter second late is invisible,
                    // and the station never charges past the mech's own
                    // maximum in the meantime.
                    if (pawn.IsHashIntervalTick(RechargeCheckInterval)
                        && ((Need)energy).CurLevel
                           >= JobGiver_GetEnergy.GetMaxRechargeLimit(pawn))
                        return JobCondition.Succeeded;
                }

                return JobCondition.Ongoing;
            });

            yield return wait;
        }
    }
}
