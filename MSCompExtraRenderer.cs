using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MechStations
{
    public class MSCompProperties_ExtraRenderer : CompProperties
    {
        // Any number of overlay layers, each a full GraphicData - so texPath,
        // graphicClass, drawSize, shader and drawOffset are all set in XML and
        // nothing about the layering is fixed in code.
        public List<GraphicData> extraGraphics = new List<GraphicData>();

        // Selectable draw positions, cycled in game through a gizmo. Meant for
        // nudging artwork against differently proportioned mechs without a
        // rebuild.
        public List<Vector3> positionPresets = new List<Vector3>();

        public MSCompProperties_ExtraRenderer()
        {
            compClass = typeof(MSCompExtraRenderer);
        }
    }

    // Draws additional graphic layers on top of a building. PRINTED into the
    // static map mesh (PostPrintOnto), not drawn per frame.
    [StaticConstructorOnStartup]
    public class MSCompExtraRenderer : ThingComp
    {
        public MSCompProperties_ExtraRenderer RendererProps =>
            (MSCompProperties_ExtraRenderer)props;

        // Per-preset clones of the XML layer GraphicDatas, with the preset
        // offset baked into drawOffset.
        private static readonly Dictionary<(ThingDef, int, int), GraphicData> _layerDataCache
            = new Dictionary<(ThingDef, int, int), GraphicData>();

        private int _presetIndex;

        private static Texture2D _iconPosition;
        private static Texture2D IconPosition =>
            _iconPosition ?? (_iconPosition = ContentFinder<Texture2D>.Get("UI/Commands/MS_Position"));

        /// <summary>
        /// Normalised preset index, safe against a shrunk preset list after a
        /// def change. Used by the building classes as their cache key part.
        /// </summary>
        public int PresetIndex
        {
            get
            {
                List<Vector3> presets = RendererProps.positionPresets;
                if (presets.NullOrEmpty()) return 0;
                return _presetIndex % presets.Count;
            }
        }

        /// <summary>
        /// Current draw offset for this building. Zero when no presets are
        /// configured, so a def without them behaves exactly as before.
        /// </summary>
        public Vector3 PositionOffset
        {
            get
            {
                List<Vector3> presets = RendererProps.positionPresets;
                if (presets.NullOrEmpty()) return Vector3.zero;
                return presets[PresetIndex];
            }
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref _presetIndex, "positionPresetIndex", 0);
        }

        // GraphicData for one printed layer under the current preset. The
        // unshifted case returns the XML object itself - no clone, no cache
        // entry, and GraphicData's own cachedGraphic does the rest.
        private GraphicData LayerData(int layerIndex)
        {
            GraphicData original = RendererProps.extraGraphics[layerIndex];

            Vector3 offset = PositionOffset;
            if (offset == Vector3.zero) return original;

            var key = (parent.def, layerIndex, PresetIndex);
            if (!_layerDataCache.TryGetValue(key, out GraphicData shifted))
            {
                shifted = new GraphicData
                {
                    texPath = original.texPath,
                    graphicClass = original.graphicClass,
                    shaderType = original.shaderType,
                    drawSize = original.drawSize,
                    color = original.color,
                    colorTwo = original.colorTwo,
                    drawOffset = original.drawOffset + offset
                };
                _layerDataCache[key] = shifted;
            }
            return shifted;
        }

        // Prints every layer into the section mesh. Same pattern as the
        // Exosuit framework's CompBuildingExtraRenderer; implemented
        // independently here with the preset baking on top.
        public override void PostPrintOnto(SectionLayer layer)
        {
            base.PostPrintOnto(layer);

            List<GraphicData> data = RendererProps.extraGraphics;
            for (int i = 0; i < data.Count; i++)
                LayerData(i).GraphicColoredFor(parent)?.Print(layer, parent, 0f);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (RendererProps.positionPresets.Count <= 1) yield break;

            yield return new Command_Action
            {
                defaultLabel = "MS_GizmoPositionLabel".Translate(),
                defaultDesc = "MS_GizmoPositionDesc".Translate(),
                icon = IconPosition,
                action = () =>
                {
                    _presetIndex = (_presetIndex + 1) % RendererProps.positionPresets.Count;

                    // Does two things at once (verified via decompile): nulls
                    // the parent's cached Graphic so the building classes'
                    // Graphic override is consulted again, and - because the
                    // def is MapMeshAndRealTime - marks the section dirty so
                    // everything printed is rebuilt with the new preset.
                    parent.Notify_ColorChanged();
                }
            };
        }

        // Textures are reloaded on a def hot-reload, so drop the clone cache;
        // the originals in RendererProps are refreshed by the reload itself.
        public override void Notify_DefsHotReloaded()
        {
            base.Notify_DefsHotReloaded();
            _layerDataCache.Clear();
        }
    }
}
