using RimWorld;

namespace MechStations
{
    // Vanilla's CompThingContainer always prints "Contents: x", counting only
    // the packs physically inside. That contradicts both the bar and what a
    // deconstruction actually drops, because the pending fraction on the
    // station is not included. MSCompMechDocking prints the honest total
    // instead, so this line is suppressed.
    //
    // Subclassing rather than patching: CompProperties_ThingContainer does not
    // set its own compClass, so the def names this class directly and nothing
    // else in the game is affected.
    public class MSCompThingContainer : CompThingContainer
    {
        public override string CompInspectStringExtra() => null;
    }
}
