using UnityEngine;
using Verse;

namespace MechStations
{
    // Draw class for station modules. Two jobs: bake the position preset into
    // the printed base graphic, and draw the linked port's waste bar.
    public class MSBuilding_StationModule : Building
    {
        private MSCompExtraRenderer _renderer;
        private bool _rendererResolved;

        // Resolved once with a flag, never with ?? - see MSBuilding_MechCharger
        // for why. This runs per FRAME.
        private MSCompExtraRenderer Renderer
        {
            get
            {
                if (!_rendererResolved)
                {
                    _rendererResolved = true;
                    _renderer = this.TryGetComp<MSCompExtraRenderer>();
                }
                return _renderer;
            }
        }

        // DrawAt runs per FRAME; the port a module faces changes at most once
        // per tick.
        private readonly MSFacedStationCache _facedStation = new MSFacedStationCache();

        public override Graphic Graphic =>
            MSDrawUtility.OffsetGraphicFor(this, Renderer) ?? base.Graphic;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            // Draws nothing itself for MapMeshAndRealTime (the base graphic
            // lives in the printed mesh, verified via decompile) - kept for
            // whatever else the base classes hook in here.
            base.DrawAt(drawLoc, flip);

            // Def-driven, exactly like on the ports: barDrawData present
            // means "show the linked station's waste level here".
            if (def.building?.barDrawData == null) return;

            // Only the port this module POINTS AT counts, so the bar never
            // shows a neighbour's waste level - see MSUtility.ModuleFacesStation.
            MSCompMechDocking station = _facedStation.Get(this);
            if (station == null) return;

            MSDrawUtility.DrawWasteBar(this, station.WastePercentFull,
                Renderer?.PositionOffset ?? Vector3.zero);
        }

        public override void Notify_DefsHotReloaded()
        {
            base.Notify_DefsHotReloaded();
            MSDrawUtility.ClearCaches();
        }
    }
}
