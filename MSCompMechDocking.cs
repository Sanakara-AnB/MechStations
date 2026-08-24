using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace MechStations
{
    public class MSCompProperties_MechDocking : CompProperties
    {
        // Empty list means "accepts every mech".
        public List<MechWeightClassDef> allowedWeightClasses = new List<MechWeightClassDef>();

        // Which module defs may be built against this port.
        public List<ThingDef> attachableModules = new List<ThingDef>();

        // No power fields here on purpose: the two levels live in vanilla's
        // CompProperties_Power as basePowerConsumption (occupied) and
        // idlePowerDraw (empty). See MSUtility.SetPowerDraw.

        // Master switch for the facing gizmo. The mech still defaults to the
        // station's own rotation when this is off.
        public bool allowMechRotation = true;

        // Whether this station accumulates wastepacks while charging.
        public bool producesWaste = true;

        public MSCompProperties_MechDocking()
        {
            compClass = typeof(MSCompMechDocking);
        }

        // The ONE place a mech's weight class is judged - job assignment, cell
        // detection and the assign gizmo all come through here. That is why the
        // Pikeman override needs no patch: see MSMechClassOverrides.
        public bool Accepts(Pawn mech)
        {
            if (mech == null) return false;
            if (allowedWeightClasses.NullOrEmpty()) return true;
            return allowedWeightClasses.Contains(
                MSMechClassOverrides.EffectiveClassFor(mech));
        }
    }

    // StaticConstructorOnStartup: this class holds static Texture2D fields.
    [StaticConstructorOnStartup]
    // Behavior comp for all Mech Stations ports: assignment, charging,
    // waste, repair, module counting, facing and gizmos.
    public class MSCompMechDocking : ThingComp
    {
        public Pawn assignedMech;

        // Exactly vanilla's Building_MechCharger value (verified via decompile:
        // a flat constant, not scaled by any stat). Accelerator modules scale
        // it through ChargeRateMultiplier.
        private const float ChargePerTick = 0.00083333335f;

        private CompPowerTrader _powerTrader;
        private Pawn _mechCache;
        private Mote _moteCharging;
        private CompGlower _glower;

        // True while a mech on the cell is actually gaining energy. Read by
        // charge accelerator modules to decide their power draw.
        public bool IsCharging { get; private set; }

        // True while a mech on the cell is actually being repaired. Read by
        // repair modules to decide their power draw.
        public bool IsRepairing { get; private set; }

        // Accumulated repair progress in vanilla mechanitor tick-equivalents.
        // Saved, so a half-finished hit point survives a reload.
        private float _repairProgress;
        private CompWasteProducer _wasteProducer;
        private CompThingContainer _wasteContainer;

        // Accumulated waste since the last pack was produced, in pack units.
        // Saved, so a half-full station stays half full across a reload.
        private float _wasteProduced;

        // Cached light state: LightOff (-1), LightIdle (-2), or an index into
        // the chargeStages list from XML.
        // Written once per tick in CompTick; the rendering class reads it
        // every frame, so the frame path stays a plain field read.
        private int _chargeLevel = LightOff;

        // Last COLOUR actually pushed to the glower, not the last state: the
        // same state maps to a different colour after a settings change, and
        // comparing states would miss that. See SetLightState.
        private ColorInt _lastGlowColor = new ColorInt(-1, -1, -1, -1);

        // -1 means "not set" -> falls back to the station's own rotation.
        private int _mechRotationIndex = -1;

        private static Texture2D _iconAssign;
        private static Texture2D _iconUnassign;
        private static Texture2D _iconRecall;

        // Indexed like Rot4.AsInt (0=North, 1=East, 2=South, 3=West).
        private static readonly Texture2D[] _iconFacing = new Texture2D[4];

        // Lazy so ContentFinder is only touched once a gizmo is actually drawn.
        private static Texture2D IconAssign =>
            _iconAssign ?? (_iconAssign = ContentFinder<Texture2D>.Get("UI/Commands/MS_Assign"));
        private static Texture2D IconUnassign =>
            _iconUnassign ?? (_iconUnassign = ContentFinder<Texture2D>.Get("UI/Commands/MS_Unassign"));
        private static Texture2D IconRecall =>
            _iconRecall ?? (_iconRecall = ContentFinder<Texture2D>.Get("UI/Commands/MS_Recall"));

        private static Texture2D IconFacing(int rotAsInt)
        {
            if (_iconFacing[rotAsInt] == null)
            {
                string[] suffix = { "North", "East", "South", "West" };
                _iconFacing[rotAsInt] = ContentFinder<Texture2D>.Get(
                    "UI/Commands/MS_FaceMech" + suffix[rotAsInt]);
            }
            return _iconFacing[rotAsInt];
        }

        public MSCompProperties_MechDocking DockingProps => (MSCompProperties_MechDocking)props;

        private bool _glowerResolved;

        // Resolved once with a flag, never with ??: a ??-latch retries on
        // every call while the comp is absent, and an absent comp costs
        // GetComp a linear walk over the whole comp list - the by-type
        // dictionary only serves exact hits (verified via decompile). Our
        // shipped defs all carry the comp, but XML is the config surface and
        // this property runs per tick.
        private CompGlower Glower
        {
            get
            {
                if (!_glowerResolved)
                {
                    _glowerResolved = true;
                    _glower = parent.TryGetComp<CompGlower>();
                }
                return _glower;
            }
        }

        /// <summary>
        /// The module building centred on a ring cell and facing this port,
        /// or null. Position equality deduplicates multi-cell modules, which
        /// occupy several ring cells but have exactly one centre.
        /// </summary>
        private Building FacingModuleAt(IntVec3 cell, Map map, CellRect stationRect)
        {
            if (!cell.InBounds(map)) return null;

            Building b = cell.GetEdifice(map);
            if (b == null || b.Position != cell) return null;

            // The edifice grid only hands out spawned buildings of THIS map,
            // so ModuleFacesStation's null/spawned/map prelude is already
            // answered - only the facing question itself remains.
            if (!MSUtility.FacesRect(b, stationRect)) return null;
            return b;
        }

        // All three module effects come from ONE ring scan per tick. Before,
        // charge rate, repair rate and waste capacity each walked the ring
        // separately, and WasteCapacity was read twice per tick - four scans
        // for one tick's worth of information.
        //
        // The tick stamp makes the cache self-invalidating: a module built,
        // switched or removed takes effect on the next tick, and reads outside
        // the tick (inspect line, gizmos) are correct too. Nothing is stored
        // across ticks, so this cannot go stale the way a registry can.
        private int _factorsTick = -1;
        private float _chargeBonus;
        private float _repairFactor;
        private float _wasteFactor;
        private int _moduleCount;

        // A module's effect changes only when one is built, destroyed or
        // switched - rescanning faster buys nothing. Measured before this
        // stamp (Dubs, 68 ports): the scan ran per tick AND per waiting mech,
        // because the job driver's end conditions read IsFullOfWaste, which
        // lands here. One shared stamp fixes every reader at once; the worst
        // case is a half-second-stale module bonus.
        private const int FactorsRescanInterval = 30;

        private void EnsureFactors()
        {
            int tick = Find.TickManager.TicksGame;
            if (_factorsTick >= 0 && tick - _factorsTick < FactorsRescanInterval)
                return;
            _factorsTick = tick;

            _chargeBonus = 0f;
            _repairFactor = 0f;
            _wasteFactor = 0f;
            _moduleCount = 0;

            WalkRing(parent.Map, release: false);
            _modulesHeld = true;
        }

        // Whether this station has told its modules they are in use since it
        // last released them. Keeps an idle port from rescanning its ring
        // every interval just to release modules it already released.
        private bool _modulesHeld;

        /// <summary>
        /// Drops every attached module to idle. The ring scan is what tells
        /// modules they are in use, and it only runs while this station has a
        /// mech - so the paths where it stops have to say so once.
        /// </summary>
        private void ReleaseModules(Map map)
        {
            if (!_modulesHeld) return;
            _modulesHeld = false;
            WalkRing(map, release: true);
        }

        // Walks the cardinal ring around this port's footprint - every cell a
        // flush-attached module's CENTRE can occupy. Corners are skipped on
        // purpose: a module in a corner cannot face the port.
        //
        // Map and rect are hoisted: Thing.Map resolves through Find.Maps[index]
        // on every read and OccupiedRect redoes rotation math - neither belongs
        // inside a 12-cell loop (the old shape of this scan cost 3.24us per
        // call, measured). The map is passed in because PostDeSpawn needs to
        // walk the ring of a map the parent no longer reports.
        private void WalkRing(Map map, bool release)
        {
            if (map == null) return;
            CellRect r = parent.OccupiedRect();

            for (int x = r.minX; x <= r.maxX; x++)
            {
                ModuleAt(new IntVec3(x, 0, r.maxZ + 1), map, r, release);
                ModuleAt(new IntVec3(x, 0, r.minZ - 1), map, r, release);
            }
            for (int z = r.minZ; z <= r.maxZ; z++)
            {
                ModuleAt(new IntVec3(r.minX - 1, 0, z), map, r, release);
                ModuleAt(new IntVec3(r.maxX + 1, 0, z), map, r, release);
            }
        }

        // Folds one ring cell into the running totals, and tells the module it
        // found what this station is doing with it. Inlined into the scan
        // rather than collecting a list first: the count is all any caller ever
        // wanted, and a shared scratch list handed back to callers would break
        // the moment two scans overlapped.
        private void ModuleAt(IntVec3 cell, Map map, CellRect stationRect,
            bool release)
        {
            Building b = FacingModuleAt(cell, map, stationRect);
            if (b == null) return;

            if (!release) _moduleCount++;

            // One walk over the module's comp list instead of three GetComp
            // lookups: two of the three would miss, and a miss falls back to a
            // linear walk anyway (verified via decompile).
            List<ThingComp> comps = b.AllComps;
            for (int i = 0; i < comps.Count; i++)
            {
                ThingComp c = comps[i];

                // Power draw is pushed, not polled: this station is already
                // holding the module and knows whether it is using it.
                if (c is MSCompStationModule module)
                {
                    if (release) module.Notify_NoLongerServed();
                    else module.Notify_ServedBy(this);
                }

                if (release) continue;

                if (c is MSCompChargeModule charge)
                {
                    if (charge.IsActive)
                        _chargeBonus += charge.ChargeProps.chargeRateBonus;
                }
                else if (c is MSCompRepairModule repair)
                {
                    if (repair.IsActive)
                        _repairFactor += repair.RepairProps.repairRateFactor;
                }
                // No IsActive check on purpose: containers are passive and
                // powerless, a built one always counts.
                else if (c is MSCompWasteContainer container)
                {
                    _wasteFactor += container.ContainerProps.capacityFactor;
                }
            }
        }

        /// <summary>
        /// Charge speed multiplier from attached accelerator modules. 1.0 with
        /// none, 1.45 with three at 15% each.
        /// </summary>
        private float ChargeRateMultiplier
        {
            get
            {
                EnsureFactors();
                return 1f + _chargeBonus;
            }
        }

        private bool _wasteProducerResolved;
        private bool _wasteContainerResolved;

        // Resolved with flags for the same reason as Glower - both run per
        // tick while a mech is charging.
        private CompWasteProducer WasteProducer
        {
            get
            {
                if (!_wasteProducerResolved)
                {
                    _wasteProducerResolved = true;
                    _wasteProducer = parent.TryGetComp<CompWasteProducer>();
                }
                return _wasteProducer;
            }
        }

        // Finds MSCompThingContainer too: GetComp falls back to an is-check
        // walk for subclasses (verified via decompile).
        private CompThingContainer WasteContainer
        {
            get
            {
                if (!_wasteContainerResolved)
                {
                    _wasteContainerResolved = true;
                    _wasteContainer = parent.TryGetComp<CompThingContainer>();
                }
                return _wasteContainer;
            }
        }

        /// <summary>
        /// Physical capacity of the built-in container.
        /// </summary>
        public int BaseWasteCapacity => WasteContainer?.Props?.stackLimit ?? 0;

        /// <summary>
        /// Total waste a charging session produces before the station blocks,
        /// container extensions included. Twice the physical container, because
        /// that is what vanilla holds: Building_MechCharger fills the container
        /// once, then counts a second load in its accumulator before
        /// IsFullOfWaste stops charging.
        /// </summary>
        public int WasteCapacity =>
            Mathf.CeilToInt(BaseWasteCapacity * VanillaCapacityCycles
                            * (1f + WasteContainerFactor));

        private const float VanillaCapacityCycles = 2f;

        /// <summary>
        /// Everything the station currently holds: physical packs in the
        /// container plus the pending fraction. Bar, block threshold and the
        /// accumulator ceiling all read THIS, so they can never disagree.
        /// </summary>
        public float WasteTotal =>
            (WasteContainer?.TotalStackCount ?? 0) + _wasteProduced;

        /// <summary>
        /// Extra capacity from linked container extensions, as a fraction of
        /// the base. Same walk pattern as ChargeRateMultiplier.
        /// </summary>
        private float WasteContainerFactor
        {
            get
            {
                EnsureFactors();
                return _wasteFactor;
            }
        }

        public float WastePercentFull =>
            WasteCapacity <= 0 ? 0f : Mathf.Clamp01(WasteTotal / WasteCapacity);

        /// <summary>
        /// Blocks charging once the station holds its full load - physical
        /// packs and pending fraction together, the same figure the bar shows.
        /// </summary>
        public bool IsFullOfWaste =>
            DockingProps.producesWaste
            && WasteCapacity > 0
            && WasteTotal >= WasteCapacity;

        /// <summary>
        /// Current light state: LightOff, LightIdle, or an index into the
        /// chargeStages list of this building's status light comp.
        /// </summary>
        public int ChargeLevel => _chargeLevel;

        // Both are NEGATIVE on purpose: any non-negative value is an index into
        // chargeStages, whose length comes from XML. A positive constant would
        // collide with a stage as soon as somebody configured that many.
        public const int LightOff  = -1;
        public const int LightIdle = -2;

        private static readonly ColorInt GlowOff = new ColorInt(0, 0, 0, 0);

        private MSCompProperties_StatusLight _lightProps;
        private bool _lightPropsResolved;

        /// <summary>
        /// The status light properties of THIS station, or null when the def
        /// carries no light comp. Source of the charge stages, which this
        /// port's own light and every attached module's light both read.
        /// </summary>
        public MSCompProperties_StatusLight LightProps
        {
            get
            {
                if (!_lightPropsResolved)
                {
                    _lightProps = parent.def.GetCompProperties<MSCompProperties_StatusLight>();
                    _lightPropsResolved = true;
                }
                return _lightProps;
            }
        }

        /// <summary>
        /// The stage a state maps to, or null for "draw nothing". Out-of-range
        /// indices yield null rather than throwing: a stage count changed in
        /// XML must never crash a save that still holds an older state.
        /// </summary>
        public MSLightStage StageForState(int state)
        {
            MSCompProperties_StatusLight props = LightProps;
            if (props == null) return null;

            if (state == LightIdle) return props.idleStage;
            if (state < 0 || state >= props.chargeStages.Count) return null;
            return props.chargeStages[state];
        }

        /// <summary>
        /// The chargeStages index for a fill level: the highest stage whose
        /// threshold is met. Walked from the top down, so the list only has to
        /// be ordered ascending - which ConfigErrors checks.
        /// </summary>
        private int StateForPercent(float pct)
        {
            MSCompProperties_StatusLight props = LightProps;
            if (props == null || props.chargeStages.Count == 0) return LightOff;

            for (int i = props.chargeStages.Count - 1; i >= 0; i--)
            {
                if (pct >= props.chargeStages[i].threshold) return i;
            }
            return LightOff;
        }

        // Entered when EITHER output wants idle light; each output then decides
        // for itself whether to show it.
        private int IdleState =>
            (MSMod.Settings.idleMask || MSMod.Settings.idleGlow)
            && LightProps?.idleStage != null
                ? LightIdle
                : LightOff;

        // Applies the glower's own two switches plus a per-def suppression.
        private ColorInt GlowColorForState(int state)
        {
            if (state == LightIdle)
            {
                if (!MSMod.Settings.idleGlow) return GlowOff;
                if (LightOverride?.suppressIdleGlow == true) return GlowOff;
            }
            else if (!MSMod.Settings.statusGlow)
            {
                return GlowOff;
            }

            return StageForState(state)?.glowColor ?? GlowOff;
        }

        private MSCompLightOverride _lightOverride;
        private bool _lightOverrideResolved;

        private MSCompProperties_LightOverride LightOverride
        {
            get
            {
                if (!_lightOverrideResolved)
                {
                    _lightOverride = parent.TryGetComp<MSCompLightOverride>();
                    _lightOverrideResolved = true;
                }
                return _lightOverride?.OverrideProps;
            }
        }

        public Color? MaskColorForState(int state)
        {
            return StageForState(state)?.maskColor;
        }

        private bool _powerTraderResolved;

        // Resolved with a flag for the same reason as Glower - IsPowered runs
        // per tick, and a station without a power comp is legal XML.
        private CompPowerTrader PowerTrader
        {
            get
            {
                if (!_powerTraderResolved)
                {
                    _powerTraderResolved = true;
                    _powerTrader = parent.TryGetComp<CompPowerTrader>();
                }
                return _powerTrader;
            }
        }

        // A station without a power comp is always considered powered.
        public bool IsPowered => PowerTrader == null || PowerTrader.PowerOn;

        /// <summary>
        /// Whether this station can actually serve the given mech right now.
        /// </summary>
        public bool IsUsableBy(Pawn mech)
        {
            if (mech == null) return false;
            if (parent == null || !parent.Spawned) return false;
            if (parent.Map != mech.Map) return false;
            if (!IsPowered) return false;
            // A full waste container stops charging just as surely as no power,
            // so a mech that actually needs energy has to look elsewhere.
            if (IsFullOfWaste) return false;
            if (!DockingProps.Accepts(mech)) return false;

            // Reservation first, on purpose. Vanilla's CanReserveAndReach runs
            // CanReach BEFORE CanReserve (verified in ReservationUtility), so a
            // port already held by another mech cost a full pathfinding query
            // before the reservation rejected it - and FindNearestFreeStation
            // can hit that several times per searching mech. CanReserve is a
            // lookup in the reservation list; running it twice is far cheaper
            // than one wasted path query.
            if (!mech.CanReserve(DockingCell)) return false;

            if (!mech.CanReserveAndReach(DockingCell, PathEndMode.OnCell, Danger.Deadly)) return false;
            return true;
        }

        // Spot model (concept 4.1): the mech stands ON the building.
        // Position is the centre cell for 1x1 and 3x3 alike, so no
        // rotation-dependent branching is needed.
        public IntVec3 DockingCell => parent.Position;

        // Direction a docked mech faces. Defaults to the station's own
        // rotation; the gizmo overrides it per station and the choice is saved,
        // so it survives the mech leaving and returning.
        public Rot4 MechFacingRotation => new Rot4(
            _mechRotationIndex >= 0 ? _mechRotationIndex : parent.Rotation.AsInt);

        /// <summary>
        /// How many modules are currently attached to this port. Comes out of
        /// the same ring scan the effect counters use, so the inspect line can
        /// never disagree with what actually takes effect.
        /// </summary>
        public int AttachedModuleCount
        {
            get
            {
                EnsureFactors();
                return _moduleCount;
            }
        }

        /// <summary>
        /// Combined repair speed from attached repair modules, as a fraction of
        /// a mechanitor's hand-repair rate. 0 with none attached.
        /// </summary>
        private float RepairRateFactor
        {
            get
            {
                EnsureFactors();
                return _repairFactor;
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            MSUtility.RegisterStation(this);
            if (assignedMech != null && !assignedMech.Dead)
                MSUtility.Register(assignedMech, this);
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            // Before unregistering, so the pending fraction still lands in the
            // container and spills with it.
            SpillPendingWaste();

            // The map comes from the parameter, not from parent.Map: this runs
            // while the port is leaving the map, and its own lookup is already
            // unreliable here.
            ReleaseModules(map);

            base.PostDeSpawn(map, mode);
            MSUtility.UnregisterStation(this);
            // Concept 4.4: removing the station releases its mech.
            ClearAssignment();
        }

        public override void PostExposeData()
        {
            base.PostExposeData();

            // Scribe_References, not Scribe_Values: the pawn already exists
            // in the map's pawn list. Saving it as an object would create a
            // duplicate on load (concept 19.1).
            Scribe_References.Look(ref assignedMech, "assignedMech");
            Scribe_Values.Look(ref _mechRotationIndex, "mechRotationIndex", -1);
            Scribe_Values.Look(ref _wasteProduced, "wasteProduced", 0f);
            Scribe_Values.Look(ref _repairProgress, "repairProgress", 0f);

            if (Scribe.mode == LoadSaveMode.PostLoadInit
                && assignedMech != null && !assignedMech.Dead)
                MSUtility.Register(assignedMech, this);
        }

        // Whether the last MechOnCell call actually produced an answer. Null
        // has TWO meanings: "nobody is standing here" and "this tick fell
        // outside the 30-tick scan window, so nobody looked". Callers that
        // discard state on an empty cell must tell them apart - see
        // UpdateRepair, which would otherwise throw away a repair progress
        // that is deliberately carried across saves.
        private bool _mechAnswerKnown;

        // Returns the mech currently standing on the docking cell, or null.
        private Pawn MechOnCell(int delta = 1)
        {
            _mechAnswerKnown = true;

            if (_mechCache != null
                && !_mechCache.Dead
                && _mechCache.Spawned
                && _mechCache.Map == parent.Map
                && _mechCache.Position == DockingCell
                && (_mechCache.pather == null || !_mechCache.pather.Moving))
                return _mechCache;

            _mechCache = null;

            if (parent.Map == null) { _mechAnswerKnown = false; return null; }
            // The delta form keeps the 30-tick cadence under interval ticking;
            // the plain form (delta 1) is what the facing gizmo calls.
            if (!parent.IsHashIntervalTick(30, delta))
            {
                _mechAnswerKnown = false;
                return null;
            }

            List<Thing> things = parent.Map.thingGrid.ThingsListAtFast(DockingCell);
            for (int i = 0; i < things.Count; i++)
            {
                if (things[i] is Pawn p
                    && !p.Dead
                    && p.IsColonyMechPlayerControlled
                    && DockingProps.Accepts(p)
                    && (p.pather == null || !p.pather.Moving))
                {
                    _mechCache = p;
                    break;
                }
            }
            return _mechCache;
        }

        // The ONLY per-tick work left on a port. The mote def sets
        // needsMaintenance without fadeOutUnmaintained, which destroys the
        // mote after a SINGLE unmaintained tick (verified via decompile and
        // Biotech's Mote_Visual.xml) - the interval below never runs more
        // often than every 6 ticks, so maintenance cannot move there.
        public override void CompTick()
        {
            _moteCharging?.Maintain();
        }

        // Everything else runs at RimWorld 1.6's adaptive rate. For a Building
        // that is every 6 ticks on camera and every 15 off camera: Thing.DoTick
        // clamps the camera rate (zoom+1, so 1-5) up against
        // Building.MinTickIntervalRate, which is 6. delta is never smaller.
        // All amounts scale by delta, so the totals stay exact -
        // the same pattern vanilla's CompRottable uses. Measured before the
        // split (Dubs, 68 ports, ~50 charging): 0.221ms per tick, 17.4% of
        // the tick budget, 3.24us per call - 27x vanilla's CompThingContainer
        // on the same buildings.
        public override void CompTickInterval(int delta)
        {
            base.CompTickInterval(delta);

            // Assignment upkeep: one null check per interval, real work every
            // 250 ticks.
            if (assignedMech != null
                && parent.IsHashIntervalTick(250, delta)
                && assignedMech.Dead)
                ClearAssignment();

            // Capacity can shrink at any time (container extension destroyed,
            // deconstructed or minified).
            if (parent.IsHashIntervalTick(250, delta))
                EjectOverflowWaste();

            Pawn mech = MechOnCell(delta);

            // Repair runs off the modules' OWN power, not the station's, so a
            // mech parked at an unpowered station still gets repaired as long
            // as a repair module has power.
            UpdateRepair(mech, delta);

            // Placed before every early return below, because those returns are
            // exactly the paths that stop scanning. With no mech, nothing calls
            // EnsureFactors any more - so this is the last chance to tell the
            // modules to stand down. With a mech the scan has already run and
            // pushed the current state.
            if (mech == null) ReleaseModules(parent.Map);

            // No power means no light of any kind, regardless of settings.
            if (!IsPowered)
            {
                _moteCharging = null;
                IsCharging = false;

                // The draw is set here too, or it would freeze at whatever was
                // last written - for a station that never had power, that is
                // the def's own basePowerConsumption. It still has to reflect
                // whether a mech is waiting: the power net reads
                // EnergyOutputPerTick when deciding what to switch back on
                // after a blackout.
                UpdatePowerOutput(occupied: mech != null);

                SetLightState(LightOff);
                return;
            }

            Need_MechEnergy energy = mech?.needs?.energy;

            if (energy == null)
            {
                UpdatePowerOutput(occupied: false);
                _moteCharging = null;
                IsCharging = false;
                SetLightState(IdleState);
                return;
            }

            UpdatePowerOutput(occupied: true);

            // One state for both outputs; the per-output switches are applied
            // where each one draws.
            if (MSMod.Settings.statusMask || MSMod.Settings.statusGlow)
                SetLightState(StateForPercent(energy.CurLevelPercentage));
            else
                SetLightState(IdleState);

            // A full waste container stops charging, same as vanilla. The mech
            // keeps standing here; it just stops gaining energy until someone
            // hauls the pack away.
            if (IsFullOfWaste)
            {
                _moteCharging = null;
                IsCharging = false;
                return;
            }

            // Charge up to the mech's own maximum.
            if (energy.CurLevel < energy.MaxLevel)
            {
                // Accelerator modules multiply the vanilla rate; delta keeps
                // the sum exact across interval ticking. Waste gets the amount
                // ACTUALLY charged - the old code passed the uncapped rate,
                // which overcounted the final tick of a session. Negligible at
                // delta 1, fifteenfold at delta 15.
                float charged = Mathf.Min(
                    ChargePerTick * ChargeRateMultiplier * delta,
                    energy.MaxLevel - energy.CurLevel);

                energy.CurLevel += charged;
                AccumulateWaste(mech, energy, charged);
                IsCharging = true;
            }
            else
            {
                IsCharging = false;
            }

            UpdateChargeMote(mech, energy);
        }

        private void UpdateChargeMote(Pawn mech, Need_MechEnergy energy)
        {
            // No camera check here. An off-camera mote is not respawned per
            // interval - CompTick maintains it every tick either way, so it
            // simply stays alive and goes undrawn, exactly as vanilla's
            // charger does it.
            //
            // Suppressed near full so the brief dip before topping off does
            // not make parked mechs flicker (concept 9.3).
            if (MSMod.Settings.suppressChargeAnimation
                || energy.CurLevelPercentage >= 0.99f)
            {
                _moteCharging = null;
                return;
            }

            if (_moteCharging == null || _moteCharging.Destroyed)
                _moteCharging = MoteMaker.MakeAttachedOverlay(
                    mech, ThingDefOf.Mote_MechCharging, Vector3.zero);

            _moteCharging?.Maintain();
        }

        // Map lighting.
        private void SetLightState(int state)
        {
            _chargeLevel = state;

            CompGlower glower = Glower;
            if (glower == null) return;

            // Compared by resulting COLOUR, not by state. GlowColorForState
            // also reads the player's glow settings, so one state can map to
            // two different colours. Comparing states left the glow burning at
            // a port whose state never changes after the setting was switched
            // off - a parked, fully charged mech is exactly that case - and
            // CompGlower saves its colour override, so the wrong colour
            // survived into the save file.
            ColorInt colour = GlowColorForState(state);
            if (colour == _lastGlowColor) return;

            _lastGlowColor = colour;
            glower.GlowColor = colour;
        }

        // Waste is tied to charging PROGRESS, not to time - exactly vanilla's
        // formula (verified via decompile):  perTick =
        // mech.GetStatValue(WastepacksPerRecharge) * (ChargePerTick /
        // energy.MaxLevel)  The bracket is this tick's share of a full charge,
        // so a big mech charges more slowly but also dirties more slowly per
        // tick, and one complete recharge always costs the same number of
        // packs.
        // Held per station, not asked per interval. Vanilla's own cache is
        // unreachable for this stat: StatWorker.SetCacheability only builds its
        // temporary cache when the StatDef sets <cacheable>, and
        // WastepacksPerRecharge does not - the cacheStaleAfterTicks argument is
        // then IGNORED without a word, which is why raising it changed nothing
        // (5.03 -> 4.74us, measurement noise).
        //
        // The walk is expensive because the def carries
        // <postProcessStatFactors><li>BandwidthCost</li>: every lookup triggers
        // a SECOND stat lookup. Measured split: 1.26us unfinalized plus 3.76us
        // finalize.
        //
        // The value only moves when bandwidth or a hediff changes, so a ten
        // second window is generous. A late value shifts the waste rate by
        // fractions of 0.0008 packs per tick - not observable in play.
        private Pawn _wasteStatMech;
        private float _wasteStatValue;
        private int _wasteStatTick = -1;
        private const int WasteStatRefreshInterval = 600;

        private float WastepacksPerRechargeOf(Pawn mech)
        {
            int tick = Find.TickManager.TicksGame;

            if (mech != _wasteStatMech
                || tick - _wasteStatTick >= WasteStatRefreshInterval)
            {
                _wasteStatMech = mech;
                _wasteStatTick = tick;
                _wasteStatValue = mech.GetStatValue(
                    StatDefOf.WastepacksPerRecharge);
            }

            return _wasteStatValue;
        }

        private void AccumulateWaste(Pawn mech, Need_MechEnergy energy, float charged)
        {
            if (!DockingProps.producesWaste) return;
            if (WasteProducer == null) return;
            if (energy.MaxLevel <= 0f) return;

            float gain = WastepacksPerRechargeOf(mech)
                            * (charged / energy.MaxLevel);

            // The ceiling counts what is already in the container, so bar,
            // block threshold and accumulator all measure the same thing.
            // Only ever ADD up to it - never pull an existing value down: a
            // surplus from a lost container module belongs to
            // EjectOverflowWaste, which spawns it on the ground. A plain Clamp
            // would swallow it before the 250-tick check ever sees it.
            float cap = WasteCapacity - (WasteContainer?.TotalStackCount ?? 0);
            if (_wasteProduced < cap)
                _wasteProduced = Mathf.Min(_wasteProduced + gain, cap);

            // Deliver a batch once one PHYSICAL container's worth has
            // accumulated and there is room for it.
            if (_wasteProduced >= BaseWasteCapacity
                && WasteContainer != null
                && !WasteContainer.Full)
            {
                _wasteProduced = Mathf.Max(0f, _wasteProduced - BaseWasteCapacity);
                WasteProducer.ProduceWaste(BaseWasteCapacity);
            }
        }

        // Deconstructing or destroying the station must not swallow the waste
        // already accumulated. Vanilla rounds UP here, so any leftover fraction
        // still becomes a full pack.
        private void SpillPendingWaste()
        {
            if (!DockingProps.producesWaste) return;
            if (WasteProducer == null) return;
            if (_wasteProduced <= 0f) return;

            int packs = Mathf.CeilToInt(_wasteProduced);
            _wasteProduced = 0f;
            WasteProducer.ProduceWaste(packs);
        }

        // Ejects whatever no longer fits after the capacity dropped below the
        // accumulated level - losing a container extension while overfilled.
        private void EjectOverflowWaste()
        {
            if (!DockingProps.producesWaste) return;
            if (parent.Map == null) return;

            int cap = WasteCapacity;
            if (WasteTotal <= cap) return;

            // Only the pending fraction is ejected - packs already sitting in
            // the container stay where they are, a colonist hauls those away.
            int packs = Mathf.Min(Mathf.CeilToInt(WasteTotal - cap),
                                  Mathf.FloorToInt(_wasteProduced));
            if (packs <= 0) return;

            _wasteProduced = Mathf.Max(0f, _wasteProduced - packs);

            int stackLimit = ThingDefOf.Wastepack.stackLimit;
            while (packs > 0)
            {
                Thing pack = ThingMaker.MakeThing(ThingDefOf.Wastepack);
                pack.stackCount = Mathf.Min(packs, stackLimit);

                // TryPlaceThing leaves the pack unspawned when it finds no
                // room, and nothing holds a reference to it afterwards - the
                // waste would vanish silently. Give the amount back instead;
                // the next 250-tick pass tries again once space appears.
                if (!GenPlace.TryPlaceThing(pack, parent.Position, parent.Map,
                                            ThingPlaceMode.Near))
                {
                    _wasteProduced += pack.stackCount;
                    break;
                }
                packs -= pack.stackCount;
            }
        }

        // Vanilla's mechanitor repairs one hit point per 120 ticks at
        // MechRepairSpeed 1 (JobDriver_RepairMech.TicksPerHeal, verified via
        // decompile).
        private const float VanillaTicksPerRepair = 120f;

        // MechRepairUtility.CanRepair is not a cheap question (verified via
        // decompile): it walks the mech's hediff list TWICE, and its
        // IsMissingWeapon branch allocates a closure on every call. Damage
        // never appears faster than half a second, so the answer is reused.
        private const int RepairCheckInterval = 30;
        private int _repairCheckTick;
        private Pawn _repairCheckMech;
        private bool _canRepair;

        private void UpdateRepair(Pawn mech, int delta)
        {
            IsRepairing = false;

            if (mech == null)
            {
                // Only discard progress when the cell was actually looked at.
                // Outside the 30-tick scan window MechOnCell returns null for
                // "did not check", and right after loading the cache is empty -
                // so an unconditional reset here wiped the very value that
                // _repairProgress is persisted for.
                if (_mechAnswerKnown)
                {
                    _repairProgress = 0f;
                    _repairCheckMech = null;
                }
                return;
            }

            // Module check BEFORE the mech check, not after: RepairRateFactor
            // reads the ring scan this tick has already paid for, so it costs a
            // tick-stamp compare. CanRepair costs two list walks and an
            // allocation. A station with no repair module must never pay that
            // just to find out it cannot repair anyway.
            float factor = RepairRateFactor;
            if (factor <= 0f)
            {
                _repairProgress = 0f;
                _repairCheckMech = null;
                return;
            }

            if (!CanRepair(mech))
            {
                _repairProgress = 0f;
                return;
            }

            IsRepairing = true;
            _repairProgress += factor * delta;

            // A loop, not an if: with delta up to 15 and factors set free in
            // XML, one interval can cross the threshold more than once.
            while (_repairProgress >= VanillaTicksPerRepair)
            {
                _repairProgress -= VanillaTicksPerRepair;

                // Same vanilla call the mechanitor uses: heals one point, or
                // restores one missing part, or regenerates a lost weapon.
                MechRepairUtility.RepairTick(mech);

                // That may have been the last point of damage. Re-ask now
                // rather than run up to RepairCheckInterval ticks on a stale
                // yes - and stop the loop on a mech that just became whole.
                RefreshCanRepair(mech);
                if (!_canRepair)
                {
                    _repairProgress = 0f;
                    break;
                }
            }
        }

        // CanRepair covers damaged parts, missing parts and a missing weapon -
        // the same set a mechanitor would work through. A mech swap forces a
        // fresh answer regardless of the interval.
        private bool CanRepair(Pawn mech)
        {
            if (_repairCheckMech != mech
                || Find.TickManager.TicksGame - _repairCheckTick >= RepairCheckInterval)
                RefreshCanRepair(mech);

            return _canRepair;
        }

        private void RefreshCanRepair(Pawn mech)
        {
            _repairCheckMech = mech;
            _repairCheckTick = Find.TickManager.TicksGame;
            _canRepair = MechRepairUtility.CanRepair(mech);
        }

        private void UpdatePowerOutput(bool occupied)
        {
            MSUtility.SetPowerDraw(PowerTrader, occupied);
        }

        private void ClearAssignment()
        {
            if (assignedMech == null) return;
            MSUtility.Unregister(assignedMech);
            assignedMech = null;
        }

        public override string CompInspectStringExtra()
        {
            string assignment;
            if (assignedMech == null)
                assignment = "MS_InspectNone".Translate();
            else if (assignedMech.Dead)
                assignment = "MS_InspectDead".Translate(assignedMech.Named("MECH"));
            else
                assignment = "MS_InspectAssigned".Translate(assignedMech.Named("MECH"));

            string modules = ModuleInspectLine();
            if (modules != null) assignment += "\n" + modules;

            if (!DockingProps.producesWaste || WasteProducer == null)
                return assignment;

            // Same line vanilla's charger shows, using the same vanilla
            // translation key - no own string needed, and it is already
            // localised in every language.
            //
            // The pack count is CeilToInt(WasteTotal), which is exactly what a
            // deconstruction drops: SpillPendingWaste rounds the pending
            // fraction up the same way. Vanilla's own "Contents" line would
            // show only the physical packs and contradict this, so
            // MSCompThingContainer suppresses it.
            return assignment + "\n"
                 + "WasteLevel".Translate() + ": " + WastePercentFull.ToStringPercent()
                 + " (" + Mathf.CeilToInt(WasteTotal) + " / " + WasteCapacity + ")";
        }

        // Cumulative effect of everything attached, replacing vanilla's
        // "connected to" line that left with the facility system.
        private string ModuleInspectLine()
        {
            int moduleCount = AttachedModuleCount;
            if (moduleCount == 0) return null;

            List<string> parts = new List<string>();

            float charge = ChargeRateMultiplier - 1f;
            if (charge > 0f)
                parts.Add("MS_InspectModuleCharge".Translate(charge.ToStringPercent()));

            float repair = RepairRateFactor;
            if (repair > 0f)
                parts.Add("MS_InspectModuleRepair".Translate(repair.ToStringPercent()));

            float waste = WasteContainerFactor;
            if (waste > 0f)
                parts.Add("MS_InspectModuleWaste".Translate(waste.ToStringPercent()));

            if (parts.Count == 0)
                return "MS_InspectModulesInactive".Translate(moduleCount);

            return "MS_InspectModules".Translate(moduleCount)
                 + ": " + string.Join(", ", parts);
        }

        // Lives on the comp, not on the building class: ThingComp has its own
        // SpecialDisplayStats hook, and ThingWithComps collects every comp's
        // contribution automatically (verified via decompile).
        public override IEnumerable<StatDrawEntry> SpecialDisplayStats()
        {
            if (DockingProps.allowedWeightClasses.NullOrEmpty())
                yield break;

            // Every mech kind this station accepts. Uses our own weight class
            // list rather than BuildingProperties.requiredMechWeightClasses,
            // which only vanilla's own charger reads.
            //
            // MSMechCompatibility.IsUsable is the part vanilla does NOT check:
            // that the player can own the mech at all, and that its think tree
            // carries our docking branch. Without it this list promised more
            // than the mod delivers - a mod mech with its own think tree would
            // appear here and then never walk to a port.
            List<PawnKindDef> compatible = DefDatabase<PawnKindDef>.AllDefs
                .Where(k => k.RaceProps != null
                         && k.RaceProps.IsMechanoid
                         && k.race?.GetCompProperties<CompProperties_OverseerSubject>() != null
                         && k.RaceProps.mechWeightClass != null
                         && DockingProps.allowedWeightClasses.Contains(k.RaceProps.mechWeightClass)
                         && MSMechCompatibility.IsUsable(k.race))
                .OrderBy(k => k.LabelCap.Resolve())
                .ToList();

            string mechList = compatible
                .Select(k => k.LabelCap.Resolve())
                .ToLineList("  - ");

            string classList = DockingProps.allowedWeightClasses
                .Select(w => w.label)
                .ToCommaList(useAnd: false)
                .CapitalizeFirst();

            yield return new StatDrawEntry(
                StatCategoryDefOf.Basics,
                "StatsReport_RechargerWeightClass".Translate(),
                classList,
                "StatsReport_RechargerWeightClass_Desc".Translate() + ": " + classList + "\n\n" + mechList,
                99999,
                null,
                compatible.Select(k => new Dialog_InfoCard.Hyperlink(k.race)));
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            // Build gizmos for everything that may attach here. Modules are
            // deliberately kept OUT of the build menu - they are only ever
            // useful against a port, so this gizmo is the single way to place
            // them.
            //
            // FindAllowedDesignator with mustBeVisible:false is what makes
            // this work. BuildCopyCommandUtility.BuildCommand itself cannot be
            // used - it looks the designator up WITH visibility, and our place
            // worker reports false so the modules stay out of the build menu -
            // but the lookup underneath it takes the flag as a parameter, and
            // DesignationCategoryDef.ResolveDesignators builds a designator for
            // every def in its category without ever consulting a place worker
            // (both verified via decompile).
            //
            // Two things come from going through vanilla instead of building
            // our own: designators are cached per def rather than allocated per
            // frame, and scenario rules (DesignatorAllowed) are honoured, which
            // a hand-built designator silently bypasses.
            //
            // Research is still checked here, because Designator_Build.Visible
            // consults the place workers and would always report false. Tech
            // level, monolith level and building prerequisites are not set on
            // any module def.
            if (parent.Faction == Faction.OfPlayer)
            {
                List<ThingDef> attachable = DockingProps.attachableModules;
                for (int i = 0; i < attachable.Count; i++)
                {
                    ThingDef moduleDef = attachable[i];
                    if (moduleDef == null) continue;

                    if (!DebugSettings.godMode && !moduleDef.IsResearchFinished)
                        continue;

                    Designator_Build des = BuildCopyCommandUtility
                        .FindAllowedDesignator(moduleDef, mustBeVisible: false);
                    if (des == null) continue;

                    // Icon fields copied the way BuildCopyCommandUtility does
                    // it, so the button looks like any other build button.
                    Command_Action build = new Command_Action
                    {
                        defaultLabel = moduleDef.LabelCap,
                        defaultDesc = moduleDef.description,
                        icon = des.ResolvedIcon(),
                        iconProportions = des.iconProportions,
                        iconDrawScale = des.iconDrawScale,
                        iconTexCoords = des.iconTexCoords,
                        iconAngle = des.iconAngle,
                        iconOffset = des.iconOffset,
                        defaultIconColor = moduleDef.uiIconColor,
                        Order = 10f,
                        action = () =>
                        {
                            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                            Find.DesignatorManager.Select(des);
                        }
                    };
                    build.SetColorOverride(des.IconDrawColor);
                    yield return build;
                }
            }

            yield return new Command_Action
            {
                defaultLabel = "MS_GizmoAssignLabel".Translate(),
                defaultDesc = "MS_GizmoAssignDesc".Translate(),
                icon = IconAssign,
                action = OpenAssignMenu
            };

            if (assignedMech != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MS_GizmoUnassignLabel".Translate(),
                    defaultDesc = "MS_GizmoUnassignDesc".Translate(),
                    icon = IconUnassign,
                    action = ClearAssignment
                };

                if (!assignedMech.Dead
                    && assignedMech.Spawned
                    && assignedMech.Map == parent.Map)
                {
                    yield return new Command_Action
                    {
                        defaultLabel = "MS_GizmoRecallLabel".Translate(),
                        defaultDesc = "MS_GizmoRecallDesc".Translate(),
                        icon = IconRecall,
                        action = RecallAssignedMech
                    };
                }
            }

            if (DockingProps.allowMechRotation)
            {
                yield return new Command_Action
                {
                    defaultLabel = "MS_GizmoFacingLabel".Translate(),
                    defaultDesc = "MS_GizmoFacingDesc".Translate(),
                    // Re-read every frame the gizmo row is drawn, so the icon
                    // always shows the direction currently selected.
                    icon = IconFacing(MechFacingRotation.AsInt),
                    action = () =>
                    {
                        if (_mechRotationIndex < 0)
                            _mechRotationIndex = parent.Rotation.AsInt;
                        _mechRotationIndex = (_mechRotationIndex + 1) % 4;

                        // Apply at once to a mech already standing here,
                        // instead of waiting for its next job.
                        Pawn here = MechOnCell();
                        if (here != null) here.Rotation = MechFacingRotation;
                    }
                };
            }
        }

        // Shared by the station gizmo and the mech-side gizmo in MSPatches.
        public void RecallAssignedMech()
        {
            if (assignedMech == null || assignedMech.jobs == null) return;

            // Same decision as every other path: below the recharge target
            // this becomes an uninterruptible charge session, otherwise a
            // parking job that yields to work.
            Job job = MSUtility.MakeDockingJob(assignedMech, this);
            assignedMech.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }

        private void OpenAssignMenu()
        {
            if (parent.Map == null) return;

            List<FloatMenuOption> options = new List<FloatMenuOption>();

            List<Pawn> mechs = parent.Map.mapPawns.AllPawnsSpawned
                .Where(p => p.IsColonyMechPlayerControlled
                         && !p.Dead
                         && DockingProps.Accepts(p))
                .OrderBy(p => p.LabelShort)
                .ToList();

            if (!mechs.Any())
            {
                options.Add(new FloatMenuOption("MS_MenuNoMechs".Translate(), null));
            }
            else
            {
                foreach (Pawn mech in mechs)
                {
                    Pawn localMech = mech;
                    MSCompMechDocking currentStation = MSUtility.FindStationForMech(mech);

                    string label = mech.LabelShort;
                    if (currentStation != null && currentStation != this)
                        label += "MS_MenuAlreadyAssigned".Translate();

                    options.Add(new FloatMenuOption(label, () =>
                    {
                        // Release the mech from its previous station (1:1
                        // rule). Through ClearAssignment, so releasing a
                        // station is ONE code path everywhere - it unregisters
                        // as well.
                        MSCompMechDocking previous = MSUtility.FindStationForMech(localMech);
                        if (previous != null && previous != this)
                            previous.ClearAssignment();
                        MSUtility.Unregister(localMech);

                        // Release this station's current mech, if any.
                        ClearAssignment();

                        assignedMech = localMech;
                        MSUtility.Register(localMech, this);
                    }));
                }
            }

            Find.WindowStack.Add(new FloatMenu(options));
        }
    }
}
