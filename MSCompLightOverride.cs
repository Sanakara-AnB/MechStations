using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MechStations
{
    // Per-def light tweaks. Purely additive: a def that wants to differ just
    // adds this comp, everything else keeps inheriting normally. Without it,
    // the building follows the palette of the port it belongs to.
    public class MSCompProperties_LightOverride : CompProperties
    {
        // Mask colours by charge state, in the order of the PORT's chargeStages.
        // A shorter list leaves the remaining states on the port's colours.
        public List<Color> maskColors = new List<Color>();

        // Idle mask colour. Omit to inherit the port's.
        public Color? idleMaskColor;

        // Turns off the texture light while idle (no mech charging).
        public bool suppressIdleMask = false;

        // Turns off the map glower while idle. Ports only - modules have none.
        public bool suppressIdleGlow = false;

        public MSCompProperties_LightOverride()
        {
            compClass = typeof(MSCompLightOverride);
        }
    }

    // Holds no state; exists so the props can be attached to a def.
    public class MSCompLightOverride : ThingComp
    {
        public MSCompProperties_LightOverride OverrideProps =>
            (MSCompProperties_LightOverride)props;
    }
}
