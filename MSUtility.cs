using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace MechStations
{
    /// <summary>
    /// One FacedStationOf lookup per TICK instead of one per frame.
    /// </summary>
    // The draw paths ask "which port am I facing" every frame, and a module is
    // asked twice per frame (status light plus waste bar). The answer can only
    // change when the map changes, so a tick stamp is enough - the same
    // self-invalidating pattern MSCompMechDocking.EnsureFactors uses.
    //
    // Instance state, never static: this holds a live comp reference, and a
    // static one would outlive the game it belongs to.
    public class MSFacedStationCache
    {
        private int _tick = -1;
        private MSCompMechDocking _station;

        // An INTERVAL, not a single tick. A one-tick window looks like caching
        // but does nothing in the ordinary case of 60 FPS against 60 TPS: every
        // frame lands in a fresh tick, so every PostDraw paid for a full grid
        // lookup after all - two Thing.Map resolutions through Find.Maps, a
        // thingGrid query and a TryGetComp, per module per frame.
        //
        // Which port a module faces only changes when something is built,
        // rotated or removed, so half a second is ample - the same cadence
        // EnsureFactors uses. A port that VANISHES is still caught immediately
        // by the spawned check below, which runs on every call.
        private const int RefreshInterval = 30;

        public MSCompMechDocking Get(Thing module)
        {
            int tick = Find.TickManager.TicksGame;
            if (_tick < 0 || tick - _tick >= RefreshInterval)
            {
                _tick = tick;
                _station = MSUtility.FacedStationOf(module);
            }

            // A port removed while the game is PAUSED would otherwise sit here
            // indefinitely - the tick stamp never advances to invalidate it.
            if (_station != null
                && (_station.parent == null || !_station.parent.Spawned))
                _station = null;

            return _station;
        }
    }

    // Runtime registry. Not saved - rebuilt from PostSpawnSetup and
    // PostExposeData(PostLoadInit) on every load.
    public static class MSUtility
    {
        // One mech belongs to at most one station (concept 4.2).
        private static readonly Dictionary<Pawn, MSCompMechDocking> _assignments
            = new Dictionary<Pawn, MSCompMechDocking>();

        private static readonly List<MSCompMechDocking> _allStations
            = new List<MSCompMechDocking>();

        /// <summary>
        /// Whether a module is FACING a given station - the single ownership
        /// criterion for the whole mod.
        /// </summary>
        public static bool ModuleFacesStation(Thing module, Thing station)
        {
            if (module == null || station == null) return false;
            if (!module.Spawned || !station.Spawned) return false;
            if (module.Map != station.Map) return false;

            return FacesRect(module, station.OccupiedRect());
        }

        /// <summary>
        /// The facing question itself, for callers that already hold the
        /// station's rect and know both things are spawned on one map (the
        /// port's ring scan: its candidates come off the map's own edifice
        /// grid). Kept HERE so the ownership criterion still lives in exactly
        /// one place - OccupiedRect recomputes rotation math on every call
        /// and must not run once per ring cell.
        /// </summary>
        public static bool FacesRect(Thing module, CellRect stationRect)
        {
            return stationRect.Contains(module.Position + module.Rotation.FacingCell);
        }

        /// <summary>
        /// The station a module points at, or null.
        /// </summary>
        public static MSCompMechDocking FacedStationOf(Thing module)
        {
            if (module == null || !module.Spawned || module.Map == null) return null;

            IntVec3 cell = module.Position + module.Rotation.FacingCell;
            return StationCompAt(cell, module.Map);
        }

        /// <summary>
        /// The port building occupying a cell, or null. Shared by the place
        /// worker and the runtime resolvers, so both use the same definition
        /// of "there is a port here".
        /// </summary>
        // Reads the edifice grid rather than the thing list: one array lookup
        // instead of a walk over everything on the cell, and the SAME question
        // MSCompMechDocking.FacingModuleAt asks. Two grid paths for one
        // question could drift apart.
        public static Building StationBuildingAt(IntVec3 cell, Map map)
            => StationCompAt(cell, map)?.parent as Building;

        /// <summary>
        /// The docking comp of the port occupying a cell, or null.
        /// </summary>
        // Callers that only want the comp used to pay TryGetComp twice: once
        // to decide whether the building counts as a port, and once on the
        // result. This is the same question, answered once.
        public static MSCompMechDocking StationCompAt(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map)) return null;
            return cell.GetEdifice(map)?.TryGetComp<MSCompMechDocking>();
        }

        /// <summary>
        /// Wipes both registries. Called from MSGameComponent's constructor,
        /// which runs on every game creation and load - the registries hold
        /// live comp references and must never survive a game switch.
        /// </summary>
        public static void ClearRegistries()
        {
            _assignments.Clear();
            _allStations.Clear();
        }

        public static void RegisterStation(MSCompMechDocking station)
        {
            if (station == null || _allStations.Contains(station)) return;
            _allStations.Add(station);
        }

        public static void UnregisterStation(MSCompMechDocking station)
        {
            if (station == null) return;
            _allStations.Remove(station);
        }

        public static void Register(Pawn mech, MSCompMechDocking station)
        {
            if (mech == null || station == null) return;
            _assignments[mech] = station;
        }

        public static void Unregister(Pawn mech)
        {
            if (mech == null) return;
            _assignments.Remove(mech);
        }

        /// <summary>
        /// The station a mech is assigned to, or null. Self-healing: an entry
        /// that no longer holds up is dropped instead of returned.
        /// </summary>
        public static MSCompMechDocking FindStationForMech(Pawn mech)
        {
            if (mech == null) return null;
            if (!_assignments.TryGetValue(mech, out MSCompMechDocking station)) return null;

            if (station?.parent == null
                || !station.parent.Spawned
                || station.parent.Map != mech.Map
                || station.assignedMech != mech)
            {
                _assignments.Remove(mech);
                return null;
            }
            return station;
        }

        // Needed by the job driver: a mech sent to a FREE station has no
        // assignment, so the station cannot be looked up by pawn.
        /// <summary>
        /// The station whose docking cell is this cell, read straight off the
        /// map rather than out of the registry.
        /// </summary>
        public static MSCompMechDocking FindStationAtCell(IntVec3 cell, Map map)
        {
            MSCompMechDocking station = StationCompAt(cell, map);
            if (station == null || station.parent.Position != cell) return null;

            return station;
        }

        public static MSCompMechDocking FindNearestFreeStation(Pawn pawn)
        {
            if (pawn?.Map == null) return null;

            MSCompMechDocking best = null;
            // Squared, because only the ORDER matters here - no square root per
            // candidate. The caller compares real distances on the winner only.
            int bestDist = int.MaxValue;

            for (int i = 0; i < _allStations.Count; i++)
            {
                MSCompMechDocking s = _allStations[i];

                // Checks run cheapest-first, and the two expensive ones only
                // for a candidate that would actually WIN. IsUsableBy ends in
                // CanReserveAndReach, i.e. a pathfinding query - running that
                // for every station on the map is what this ordering avoids.
                if (s?.parent == null || !s.parent.Spawned) continue;

                // An assigned station is reserved for its own mech, full stop
                // - including while that mech is off the map on a caravan.
                if (s.assignedMech != null && s.assignedMech != pawn)
                    continue;

                int d = pawn.Position.DistanceToSquared(s.DockingCell);
                if (d >= bestDist) continue;

                // The registry is still the enumeration source here - there is
                // no cheap way to sweep a whole map for ports - but every
                // candidate is validated against the grid before it can be
                // handed out.
                if (FindStationAtCell(s.DockingCell, pawn.Map) != s) continue;
                if (!s.IsUsableBy(pawn)) continue;

                bestDist = d;
                best = s;
            }
            return best;
        }

        /// <summary>
        /// Single decision point for every path that sends a mech to a station:
        /// the recall gizmos, the idle think-tree giver and the energy-branch
        /// patch all go through here, so they behave identically.
        /// </summary>
        public static Job MakeDockingJob(Pawn mech, MSCompMechDocking station)
        {
            Need_MechEnergy energy = mech?.needs?.energy;

            // A station that cannot currently charge hands out a PARKING job
            // instead of a charge session. Two reasons it may not be able to:
            // no power, or a full waste container.
            bool needsCharge = station.IsPowered
                && !station.IsFullOfWaste
                && energy != null
                && ((Need)energy).CurLevel < JobGiver_GetEnergy.GetMaxRechargeLimit(mech);

            Job job = JobMaker.MakeJob(
                needsCharge ? MSJobDefOf.MS_DockingCharge : MSJobDefOf.MS_DockingWait,
                station.DockingCell);

            if (needsCharge)
            {
                // No expiry at all: the think tree is never consulted while
                // charging.
                job.expiryInterval = 0;
            }
            else
            {
                // Vanilla uses 30-50 for standing-around jobs. checkOverrideOnExpire
                // means the job is not ended at expiry, only re-evaluated.
                job.checkOverrideOnExpire = true;
                job.expiryInterval = 60;
            }

            return job;
        }

        /// <summary>
        /// Switches a trader between vanilla's active and idle power level.
        /// Ports and modules both go through here.
        /// </summary>
        // Vanilla already carries a two-level draw: basePowerConsumption is the
        // active load, idlePowerDraw the resting one. Reading those instead of
        // a mod-owned pair is what makes the inspect line, its "(x W when
        // active)" suffix and the stat entry agree with reality - and it is why
        // the line disappears when a building is flicked off, because
        // CompPowerTrader tests idlePowerDraw for exactly that.
        public static void SetPowerDraw(CompPowerTrader power, bool active)
        {
            if (power == null) return;

            // idlePowerDraw defaults to -1 meaning "no idle level"; negating
            // that would have the building GENERATE a watt.
            float idle = power.Props.idlePowerDraw > 0f
                ? power.Props.idlePowerDraw
                : 0f;

            power.PowerOutput = active ? -power.Props.PowerConsumption : -idle;
        }
    }
}
