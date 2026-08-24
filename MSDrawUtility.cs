using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MechStations
{
    // Draw helpers shared by both building classes. Ports and modules draw the
    // SAME waste bar and bake position presets the SAME way - one function per
    // question, so the two can never drift apart.
    [StaticConstructorOnStartup]
    public static class MSDrawUtility
    {
        // Single source for the bar colours - the module bar reads identically
        // to the port bar because it IS the port bar. Values match vanilla's
        // Building_MechCharger (verified via decompile).
        private static readonly Material WasteBarFilled =
            SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.9f, 0.85f, 0.2f));
        private static readonly Material WasteBarUnfilled =
            SolidColorMaterials.SimpleSolidColorMaterial(new Color(0.3f, 0.3f, 0.3f, 1f));

        // Position-preset clones of a def's base GraphicData, offset baked in
        // because the print path takes no runtime offset.
        //
        // CACHE KEY DISCIPLINE: keys must stay finite and XML-defined (def,
        // preset index). GraphicRequest hashes the GraphicData instance and
        // GraphicDatabase never evicts, so a computed key would leak one clone
        // per lookup.
        private static readonly Dictionary<(ThingDef, int), GraphicData> _offsetDataCache
            = new Dictionary<(ThingDef, int), GraphicData>();

        /// <summary>
        /// The building's base graphic with the current position preset baked
        /// into its drawOffset, or null when no offset applies - callers fall
        /// back to base.Graphic, so vanilla handles stuff, paint and style.
        /// </summary>
        public static Graphic OffsetGraphicFor(Building b, MSCompExtraRenderer renderer)
        {
            Vector3 offset = renderer?.PositionOffset ?? Vector3.zero;
            if (offset == Vector3.zero) return null;

            var key = (b.def, renderer.PresetIndex);
            if (!_offsetDataCache.TryGetValue(key, out GraphicData gd))
            {
                // Copied field by field so the variant behaves exactly like the
                // def's own graphic apart from its baked position. A null
                // shaderType is fine: GraphicData.Init falls back to Cutout
                // itself (verified via decompile). Per-rotation offsets
                // (drawOffsetEast etc.) are not copied - decided limitation, no
                // def sets them.
                gd = new GraphicData
                {
                    texPath = b.def.graphicData.texPath,
                    graphicClass = b.def.graphicData.graphicClass,
                    shaderType = b.def.graphicData.shaderType,
                    drawSize = b.def.graphicData.drawSize,
                    color = b.def.graphicData.color,
                    colorTwo = b.def.graphicData.colorTwo,
                    drawOffset = b.def.graphicData.drawOffset + offset
                };
                _offsetDataCache[key] = gd;
            }

            // GraphicColoredFor keeps player paint working on shifted variants;
            // colour versions are deduplicated by GraphicDatabase against the
            // finite paint palette (verified via decompile).
            return gd.GraphicColoredFor(b);
        }

        /// <summary>
        /// Draws the waste bar for a building whose def carries barDrawData.
        /// Size, margin and placement come from the def, one entry per
        /// rotation. The +0.1 lift clears every printed layer including the
        /// module tops.
        /// </summary>
        public static void DrawWasteBar(Building b, float fillPercent, Vector3 offset)
        {
            GenDraw.FillableBarRequest r = b.def.building.BarDrawDataFor(b.Rotation);
            r.center = b.DrawPos + Vector3.up * 0.1f + offset;
            r.fillPercent = fillPercent;
            r.filledMat = WasteBarFilled;
            r.unfilledMat = WasteBarUnfilled;
            r.rotation = b.Rotation;
            GenDraw.DrawFillableBar(r);
        }

        // Textures are reloaded on a def hot-reload, so the clones must be
        // rebuilt. Called from both building classes; clearing twice is
        // harmless.
        public static void ClearCaches()
        {
            _offsetDataCache.Clear();
        }
    }
}
