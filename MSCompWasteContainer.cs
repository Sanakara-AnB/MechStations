using Verse;

namespace MechStations
{
    public class MSCompProperties_WasteContainer : CompProperties, IMSAttachableModuleProps
    {
        // Extra waste capacity granted to the linked station, as a fraction of
        // the station's own base capacity. 1.0 doubles it.
        public float capacityFactor = 1.0f;

        // Enforced by the place worker; see IMSAttachableModuleProps.
        public int maxPerStation = 1;

        public int MaxPerStation => maxPerStation;

        public MSCompProperties_WasteContainer()
        {
            compClass = typeof(MSCompWasteContainer);
        }
    }

    // Waste capacity extension for a port.
    public class MSCompWasteContainer : ThingComp
    {
        public MSCompProperties_WasteContainer ContainerProps =>
            (MSCompProperties_WasteContainer)props;

        // CompInspectStringExtra runs per frame while the container is
        // selected, so the port lookup behind it is cached per tick.
        private readonly MSFacedStationCache _facedStation = new MSFacedStationCache();

        /// <summary>
        /// The docking comp of the port this container faces, or null while
        /// unlinked.
        /// </summary>
        public MSCompMechDocking LinkedStation
        {
            get
            {
                // Only the port this container POINTS AT counts - see
                // MSUtility.ModuleFacesStation for why adjacency alone is
                // not enough.
                return _facedStation.Get(parent);
            }
        }

        public override string CompInspectStringExtra()
        {
            MSCompMechDocking station = LinkedStation;
            if (station == null) return "MS_InspectWasteUnlinked".Translate();

            return "MS_InspectWasteContainer".Translate(
                station.WastePercentFull.ToStringPercent(),
                ContainerProps.capacityFactor.ToStringPercent());
        }
    }
}
