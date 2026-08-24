using Verse;

namespace MechStations
{
    public class MSCompProperties_ChargeModule : MSCompProperties_StationModule
    {
        // Speed added to the served port's charge rate, per module.
        // Three large modules at 0.15 give +45%, two small ones +30% -
        // maxPerStation in the def decides how many may be built.
        public float chargeRateBonus = 0.15f;

        public MSCompProperties_ChargeModule()
        {
            compClass = typeof(MSCompChargeModule);
        }
    }

    // Charge accelerator: speeds up charging at the station it is attached to.
    public class MSCompChargeModule : MSCompStationModule
    {
        public MSCompProperties_ChargeModule ChargeProps =>
            (MSCompProperties_ChargeModule)props;

        protected override string ToggleLabelKey => "MS_GizmoBoostLabel";
        protected override string ToggleDescKey => "MS_GizmoBoostDesc";

        // Only draws the higher power while a mech is genuinely gaining energy.
        protected override bool StationIsUsingModule(MSCompMechDocking station)
            => station.IsCharging;

        public override string CompInspectStringExtra()
        {
            if (!IsPowered) return null;
            return IsActive
                ? "MS_InspectBoostOn".Translate(ChargeProps.chargeRateBonus.ToStringPercent())
                : "MS_InspectBoostOff".Translate();
        }
    }
}
