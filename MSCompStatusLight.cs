using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace MechStations
{
    // One charge stage: from which fill level it applies, and the two colours
    // it drives. Both outputs switch at the same moment but may look different.
    public class MSLightStage
    {
        // Lower bound as a fraction of the mech's energy, inclusive.
        public float threshold = 0f;

        // Multiplied onto the "{texPath}_light" mask, so paint the mask white.
        public Color maskColor = Color.white;

        // Map glower colour, 0-255. Alpha unused by vanilla convention.
        public ColorInt glowColor = new ColorInt(255, 255, 255, 0);
    }

    public class MSCompProperties_StatusLight : CompProperties
    {
        // Absolute height above the def's altitudeLayer, like the waste bar.
        // 0.075 clears the printed port top (0.06, plus 0.01 of print tilt) and
        // stays below the module tops at 0.08.
        public float lightAltitude = 0.075f;

        // The LENGTH of this list is the number of charge states. Ports only;
        // modules read state, thresholds and colours from the port they face.
        public List<MSLightStage> chargeStages = new List<MSLightStage>();

        // Optional state for "powered, but showing no charge stage". No
        // threshold, because it depends on the situation, not on a fill level.
        public MSLightStage idleStage;

        public MSCompProperties_StatusLight()
        {
            compClass = typeof(MSCompStatusLight);
        }

        public override IEnumerable<string> ConfigErrors(ThingDef parentDef)
        {
            foreach (string e in base.ConfigErrors(parentDef)) yield return e;

            for (int i = 1; i < chargeStages.Count; i++)
            {
                if (chargeStages[i].threshold <= chargeStages[i - 1].threshold)
                    yield return "MSCompProperties_StatusLight: chargeStages must "
                               + "ascend by threshold (entry " + i + ").";
            }

            if (chargeStages.Count > 0 && chargeStages[0].threshold > 0f)
                yield return "MSCompProperties_StatusLight: first stage should "
                           + "start at threshold 0.";
        }
    }

    // Charge-state light overlay. Any of our buildings can carry it.
    //
    // Three switches, all outside code: the comp makes a light possible, a
    // "{texPath}_light" mask on disk makes it visible, and chargeStages decides
    // how many states there are. A def may add MSCompProperties_LightOverride
    // to change its own colours without touching the state machine.
    //
    // State comes from the building's own docking comp, or from the port it
    // faces - so port and module always glow in step.
    //
    // Drawn in PostDraw, which ThingWithComps.DrawAt calls unconditionally, so
    // this works on printed and realtime buildings alike.
    public class MSCompStatusLight : ThingComp
    {
        // CACHE KEY DISCIPLINE: keys must stay finite and XML-defined. The
        // source def is part of the key because the colours may come from the
        // linked port, and the same module def can face differently coloured
        // ports.
        private static readonly Dictionary<(ThingDef, ThingDef, int), Graphic> _lightCache
            = new Dictionary<(ThingDef, ThingDef, int), Graphic>();
        private static readonly HashSet<(ThingDef, ThingDef)> _lightChecked
            = new HashSet<(ThingDef, ThingDef)>();

        private MSCompMechDocking _ownDocking;
        private bool _ownDockingResolved;
        private MSCompExtraRenderer _renderer;
        private MSCompLightOverride _override;
        private bool _overrideResolved;

        // Modules have no docking comp of their own and must look their port up
        // on the map. PostDraw runs per FRAME, so that lookup is cached per
        // tick - ports never touch it, OwnDocking answers first.
        private readonly MSFacedStationCache _facedStation = new MSFacedStationCache();

        public MSCompProperties_StatusLight LightProps =>
            (MSCompProperties_StatusLight)props;

        // Resolved once with a flag: modules legitimately have no docking comp,
        // and retrying every frame would defeat the caching.
        private MSCompMechDocking OwnDocking
        {
            get
            {
                if (!_ownDockingResolved)
                {
                    _ownDocking = parent.TryGetComp<MSCompMechDocking>();
                    _ownDockingResolved = true;
                }
                return _ownDocking;
            }
        }

        private MSCompProperties_LightOverride Overrides
        {
            get
            {
                if (!_overrideResolved)
                {
                    _override = parent.TryGetComp<MSCompLightOverride>();
                    _overrideResolved = true;
                }
                return _override?.OverrideProps;
            }
        }

        private bool _rendererResolved;

        // Resolved once with a flag, never with ?? - same discipline as
        // OwnDocking above, and for the same reason: this runs per FRAME, and
        // a ??-latch would retry TryGetComp forever on a def without the comp.
        private MSCompExtraRenderer Renderer
        {
            get
            {
                if (!_rendererResolved)
                {
                    _rendererResolved = true;
                    _renderer = parent.TryGetComp<MSCompExtraRenderer>();
                }
                return _renderer;
            }
        }

        // The docking comp whose state and colours drive this light.
        private MSCompMechDocking StateSource =>
            OwnDocking ?? _facedStation.Get(parent);

        private void EnsureGraphics(MSCompMechDocking source, string texBase)
        {
            var key = (parent.def, source.parent.def);
            if (_lightChecked.Contains(key)) return;
            _lightChecked.Add(key);

            MSCompProperties_StatusLight props = source.LightProps;
            if (props == null) return;

            for (int state = 0; state < props.chargeStages.Count; state++)
                CacheStage(source, texBase, state);

            if (props.idleStage != null || Overrides?.idleMaskColor != null)
                CacheStage(source, texBase, MSCompMechDocking.LightIdle);
        }

        private void CacheStage(MSCompMechDocking source, string texBase, int state)
        {
            Color? tint = MaskColorFor(source, state);
            _lightCache[(parent.def, source.parent.def, state)] =
                tint.HasValue ? TryLoadLight(texBase + "_light", tint.Value) : null;
        }

        // This def's own override if it has one, otherwise the state source's
        // colour. Null means "draw nothing for this state".
        private Color? MaskColorFor(MSCompMechDocking source, int state)
        {
            MSCompProperties_LightOverride ov = Overrides;

            if (state == MSCompMechDocking.LightIdle)
            {
                if (ov != null)
                {
                    if (ov.suppressIdleMask) return null;
                    if (ov.idleMaskColor.HasValue) return ov.idleMaskColor.Value;
                }
            }
            else if (ov != null && state >= 0 && state < ov.maskColors.Count)
            {
                return ov.maskColors[state];
            }

            return source.MaskColorForState(state);
        }

        private Graphic TryLoadLight(string path, Color tint)
        {
            // Graphic_Multi appends the direction suffix itself, so the north
            // variant decides whether the mask exists at all.
            if (ContentFinder<Texture2D>.Get(path + "_north", reportFailure: false) == null)
                return null;

            // Transparent, not the def's CutoutComplex: a hard alpha cutoff
            // would destroy the soft edges a light mask needs.
            return GraphicDatabase.Get<Graphic_Multi>(
                path, ShaderDatabase.Transparent, parent.def.graphicData.drawSize, tint);
        }

        // Per-instance resolution for the per-frame path. The static caches
        // below stay the loaders, but their tuple keys hash on every lookup -
        // too much for PostDraw. This array is indexed by charge state (idle
        // in the last slot) and rebuilt only when the state source's DEF
        // changes, which in practice means never.
        private ThingDef _resolvedSourceDef;
        private Graphic[] _resolvedByState;

        private Graphic LightForState(MSCompMechDocking source, int state)
        {
            if (_resolvedSourceDef != source.parent.def)
                BuildStateGraphics(source);

            int stages = _resolvedByState.Length - 1;
            if (state == MSCompMechDocking.LightIdle)
                return _resolvedByState[stages];

            // Out-of-range guard: a stage count shrunk in XML must never
            // crash - or mis-colour - a save that still holds an older state.
            if (state < 0 || state >= stages) return null;
            return _resolvedByState[state];
        }

        private void BuildStateGraphics(MSCompMechDocking source)
        {
            ThingDef sourceDef = source.parent.def;
            _resolvedSourceDef = sourceDef;

            EnsureGraphics(source, parent.def.graphicData.texPath);

            int stages = source.LightProps?.chargeStages.Count ?? 0;
            _resolvedByState = new Graphic[stages + 1];
            for (int state = 0; state < stages; state++)
                _lightCache.TryGetValue((parent.def, sourceDef, state),
                    out _resolvedByState[state]);
            _lightCache.TryGetValue(
                (parent.def, sourceDef, MSCompMechDocking.LightIdle),
                out _resolvedByState[stages]);
        }

        public override void PostDraw()
        {
            base.PostDraw();

            // Both mask switches off means there is nothing to draw - and so
            // nothing to look up either. Asked FIRST, before StateSource, or a
            // module would still hunt for its port every frame just to throw
            // the answer away. That makes the setting an actual relief on a
            // weak machine instead of a mere visual preference.
            if (!MSMod.Settings.idleMask && !MSMod.Settings.statusMask) return;

            MSCompMechDocking source = StateSource;
            if (source == null) return;

            int state = source.ChargeLevel;
            if (state == MSCompMechDocking.LightOff) return;

            // The per-state switch still has to be checked here: which of the
            // two applies is only known once the state is.
            bool idle = state == MSCompMechDocking.LightIdle;
            if (idle && !MSMod.Settings.idleMask) return;
            if (!idle && !MSMod.Settings.statusMask) return;

            Graphic light = LightForState(source, state);
            if (light == null) return;

            // Follows the position preset like every other layer.
            Vector3 pos = parent.DrawPos + (Renderer?.PositionOffset ?? Vector3.zero);

            // Absolute height from the layer ladder, NOT relative to the def's
            // own drawOffset - the mask sits on its rung, not on its building.
            // Horizontal drawOffset is not compensated either: GraphicDatabase.Get
            // leaves Graphic.data null, so Draw adds no offset of its own. Every
            // def currently uses (0, y, 0), so only the height ever mattered.
            pos.y = parent.def.altitudeLayer.AltitudeFor() + LightProps.lightAltitude;

            light.Draw(pos, parent.Rotation, parent);
        }

        // Runs once per comp instance (ThingWithComps propagates it, verified
        // via decompile) - the static clears are redundant after the first
        // instance but harmless.
        public override void Notify_DefsHotReloaded()
        {
            base.Notify_DefsHotReloaded();
            _lightCache.Clear();
            _lightChecked.Clear();
            _resolvedSourceDef = null;
            _resolvedByState = null;
        }
    }
}
