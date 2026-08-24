using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace MechStations
{
    // Implemented by every attachable module's props, so the place worker
    // can read the per-port limit without knowing the concrete comp type.
    public interface IMSAttachableModuleProps
    {
        int MaxPerStation { get; }
    }

    // Shared properties for every station module. Concrete modules add their
    // own effect strength field on top.
    public abstract class MSCompProperties_StationModule : CompProperties, IMSAttachableModuleProps
    {
        // How many modules of this def may serve one port. Enforced by the
        // place worker (built + blueprints + frames).
        public int maxPerStation = 3;

        public int MaxPerStation => maxPerStation;

        // No power fields here on purpose: the two levels live in vanilla's
        // CompProperties_Power as basePowerConsumption (active) and
        // idlePowerDraw (resting). See MSUtility.SetPowerDraw.
    }

    // Shared behaviour for station modules: power switching, the on/off gizmo,
    // and finding the stations this module is attached to.
    [StaticConstructorOnStartup]
    public abstract class MSCompStationModule : ThingComp
    {
        // Player switch. Off means the module keeps drawing idle power but
        // contributes nothing - use vanilla's flick switch to cut it entirely.
        private bool _enabled = true;

        private CompPowerTrader _powerTrader;

        private static Texture2D _iconToggle;
        protected static Texture2D IconToggle =>
            _iconToggle ?? (_iconToggle = ContentFinder<Texture2D>.Get("UI/Commands/MS_ModuleToggle"));

        private bool _powerTraderResolved;

        // Resolved once with a flag, never with ?? - see MSCompMechDocking's
        // Glower for why. Only every 30 ticks here, kept for consistency.
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

        protected bool IsPowered => PowerTrader == null || PowerTrader.PowerOn;

        public bool IsActive => _enabled && IsPowered;

        // Label and description keys for the toggle gizmo.
        protected abstract string ToggleLabelKey { get; }
        protected abstract string ToggleDescKey { get; }

        /// <summary>
        /// Whether a station this module is attached to is currently making use
        /// of it. Drives the higher power draw, so an idle module stays cheap.
        /// </summary>
        protected abstract bool StationIsUsingModule(MSCompMechDocking station);

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref _enabled, "moduleEnabled", true);
        }

        // Whether the station this module serves is currently using it, and
        // when it last said so. Both are pushed in by the station and never
        // derived here - the station already holds the answer while it scans
        // its ring, and a module asking a second time is the same question
        // answered by two paths.
        private bool _served;
        private int _servedTick = -1;

        /// <summary>
        /// Called by the station this module faces, once per ring scan.
        /// </summary>
        public void Notify_ServedBy(MSCompMechDocking station)
        {
            _servedTick = Find.TickManager.TicksGame;
            SetServed(StationIsUsingModule(station));
        }

        /// <summary>
        /// Called when the serving station stops using this module - it lost
        /// its mech, or it is being removed from the map.
        /// </summary>
        // Deliberately not named Notify_Released: ThingComp already has one,
        // called from an unrelated context, and hiding it would be a trap.
        public void Notify_NoLongerServed()
        {
            _servedTick = Find.TickManager.TicksGame;
            SetServed(false);
        }

        private void SetServed(bool served)
        {
            if (_served == served) return;
            _served = served;
            UpdatePowerOutput();
        }

        // Safety net, and the only reason this comp ticks at all - hence Rare
        // rather than Normal. A module whose station vanished without a word
        // would otherwise keep drawing the active load forever. It never asks
        // WHICH station serves it; that question has exactly one owner now.
        private const int OrphanedAfterTicks = 250;

        public override void CompTickRare()
        {
            base.CompTickRare();

            // Orphan check first, so a state change here does not make the
            // rewrite below run twice.
            if (_served
                && Find.TickManager.TicksGame - _servedTick > OrphanedAfterTicks)
                SetServed(false);

            // Unconditional, and NOT redundant: vanilla overwrites PowerOutput
            // behind our back. CompPower.SetUpPowerVars runs on spawn and on
            // every power net re-registration, and it writes the FULL active
            // load whenever PowerOn is true - which it is right after loading,
            // because powerOnInt is saved. SetServed cannot repair that on its
            // own: it returns early when the state has not changed, and at a
            // port without a mech the state never changes. Without this line
            // every module would draw its active watts forever after a load.
            UpdatePowerOutput();
        }

        protected void UpdatePowerOutput()
        {
            MSUtility.SetPowerDraw(PowerTrader, IsActive && _served);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Command_Toggle
            {
                defaultLabel = ToggleLabelKey.Translate(),
                defaultDesc = ToggleDescKey.Translate(),
                icon = IconToggle,
                isActive = () => _enabled,
                toggleAction = () =>
                {
                    _enabled = !_enabled;
                    UpdatePowerOutput();
                }
            };
        }
    }
}
