using RimWorld;
using Verse;

namespace MechStations
{
    [DefOf]
    public static class MSJobDefOf
    {
        // Charge session - blocks work until the recharge target is reached.
        public static JobDef MS_DockingCharge;

        // Parking - mech is topped up and available for work.
        public static JobDef MS_DockingWait;

        static MSJobDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(MSJobDefOf));
        }
    }
}
