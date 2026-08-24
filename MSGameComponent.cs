using Verse;

namespace MechStations
{
    // Clears our static caches whenever a game is created.
    public class MSGameComponent : GameComponent
    {
        // Required by RimWorld's component instantiation.
        public MSGameComponent(Game game)
        {
            MSUtility.ClearRegistries();
            MSMechClassOverrides.Clear();
        }
    }
}
