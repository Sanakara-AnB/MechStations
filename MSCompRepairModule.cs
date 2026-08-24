using Verse;

namespace MechStations
{
    public class MSCompProperties_RepairModule : MSCompProperties_StationModule
    {
        // Repair speed as a fraction of what a mechanitor achieves by hand.
        // 0.30 means roughly 400 ticks per hit point instead of vanilla's 120.
        public float repairRateFactor = 0.30f;

        public MSCompProperties_RepairModule()
        {
            compClass = typeof(MSCompRepairModule);
        }
    }

    // Auto-repair unit: slowly restores a docked mech without anyone tending to
    // it. The trade-off is exactly that - far slower than a mechanitor and it
    // costs power, but it needs no handling at all.
    public class MSCompRepairModule : MSCompStationModule
    {
        public MSCompProperties_RepairModule RepairProps =>
            (MSCompProperties_RepairModule)props;

        protected override string ToggleLabelKey => "MS_GizmoRepairLabel";
        protected override string ToggleDescKey => "MS_GizmoRepairDesc";

        // Only draws the higher power while there is actually damage to fix.
        protected override bool StationIsUsingModule(MSCompMechDocking station)
            => station.IsRepairing;

        public override string CompInspectStringExtra()
        {
            if (!IsPowered) return null;
            return IsActive
                ? "MS_InspectRepairOn".Translate(RepairProps.repairRateFactor.ToStringPercent())
                : "MS_InspectRepairOff".Translate();
        }
    }
}
