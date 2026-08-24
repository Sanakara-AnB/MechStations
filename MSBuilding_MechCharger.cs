using UnityEngine;
using Verse;

namespace MechStations
{
    // drawerType is MapMeshAndRealTime: the base graphic and the extra layers
    // are PRINTED, only the waste bar is drawn per frame. Thing.DrawAt draws
    // the base graphic itself only for RealtimeOnly (verified via decompile),
    // so there is no double draw.
    public class MSBuilding_MechCharger : Building
    {
        private MSCompMechDocking _dockComp;
        private bool _dockCompResolved;
        private MSCompExtraRenderer _renderer;
        private bool _rendererResolved;

        // Resolved once with a flag, never with ??: a ??-latch retries forever
        // when the comp is absent, and an absent comp costs GetComp a linear
        // walk over every comp on the thing (verified via decompile - the
        // by-type dictionary only serves exact hits). These run per FRAME.
        private MSCompMechDocking DockComp
        {
            get
            {
                if (!_dockCompResolved)
                {
                    _dockCompResolved = true;
                    _dockComp = this.TryGetComp<MSCompMechDocking>();
                }
                return _dockComp;
            }
        }

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

        // Read by Thing.Print for the map mesh. The preset offset is baked in,
        // so switching presets only needs Notify_ColorChanged to re-fetch and
        // reprint.
        public override Graphic Graphic =>
            MSDrawUtility.OffsetGraphicFor(this, Renderer) ?? base.Graphic;

        protected override void DrawAt(Vector3 drawLoc, bool flip = false)
        {
            // The dynamic layers follow the position gizmo, which the printed
            // artwork already carries baked into its Graphic.
            Vector3 offset = Renderer?.PositionOffset ?? Vector3.zero;
            base.DrawAt(drawLoc + offset, flip);

            // No barDrawData in the def means no bar.
            MSCompMechDocking comp = DockComp;
            if (comp != null && def.building?.barDrawData != null)
                MSDrawUtility.DrawWasteBar(this, comp.WastePercentFull, offset);

            // The status light draws itself in MSCompStatusLight.PostDraw,
            // which runs right after this method.
        }

        public override void Notify_DefsHotReloaded()
        {
            base.Notify_DefsHotReloaded();
            MSDrawUtility.ClearCaches();
        }
    }
}
