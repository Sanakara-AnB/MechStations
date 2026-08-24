using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace MechStations
{
    // Global player preferences. Anything that varies per building lives in
    // XML on the comp instead.
    public class MSSettings : ModSettings
    {
        // Suppresses the vanilla charging mote on mechs docked at our stations.
        public bool suppressChargeAnimation = false;

        // Four independent switches: texture mask and map glower, each for the
        // charge states and for idle (powered, nothing charging). All four
        // default to ON - a fresh install should show what the mod can do.
        // The def-side switches still win: a building without a _light mask or
        // with suppressIdleMask stays dark regardless.
        public bool idleMask = true;
        public bool idleGlow = true;
        public bool statusMask = true;
        public bool statusGlow = true;

        // Lets oversized mechs count as a heavier class on our ports only -
        // see MSMechClassOverrideDef. Vanilla's Pikeman is the shipped case.
        public bool applyMechClassOverrides = true;

        // Only the labels the player switched OFF. Keyed by optionLabelKey and
        // not by defName, because one checkbox can cover several entries - a
        // mod that ships two oversized mechs gets one box named after the mod,
        // not two boxes with the same caption.
        //
        // Storing the exceptions rather than every state keeps the default at
        // "on" without writing a line per entry, and an entry whose mod was
        // uninstalled simply stops being asked about.
        private List<string> disabledOverrides = new List<string>();

        public bool OverrideEnabled(MSMechClassOverrideDef def)
        {
            if (def == null) return false;
            if (!def.HasOwnToggle) return true;
            return ToggleEnabled(def.optionLabelKey);
        }

        public bool ToggleEnabled(string labelKey) =>
            !disabledOverrides.Contains(labelKey);

        public void SetToggleEnabled(string labelKey, bool on)
        {
            if (labelKey.NullOrEmpty()) return;

            if (on) disabledOverrides.Remove(labelKey);
            else if (!disabledOverrides.Contains(labelKey))
                disabledOverrides.Add(labelKey);
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref suppressChargeAnimation, "suppressChargeAnimation", false);
            Scribe_Values.Look(ref idleMask, "idleMask", true);
            Scribe_Values.Look(ref idleGlow, "idleGlow", true);
            Scribe_Values.Look(ref statusMask, "statusMask", true);
            Scribe_Values.Look(ref statusGlow, "statusGlow", true);
            Scribe_Values.Look(ref applyMechClassOverrides, "applyMechClassOverrides", true);

            Scribe_Collections.Look(ref disabledOverrides, "disabledOverrides",
                LookMode.Value);
            // An empty list is written as nothing at all, so a settings file
            // saved with every box ticked reads back as null.
            if (disabledOverrides == null)
                disabledOverrides = new List<string>();

            base.ExposeData();
        }
    }

    public class MSMod : Mod
    {
        public static MSSettings Settings;

        public MSMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MSSettings>();

            // PatchAll scans this assembly for [HarmonyPatch] attributes.
            new Harmony("sanakara.mechstations").PatchAll();

            Log.Message("[MechStations] Initialized.");
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("MS_SettingSuppressChargeAnimation".Translate(),
                ref Settings.suppressChargeAnimation);
            listing.CheckboxLabeled("MS_SettingStatusMask".Translate(),
                ref Settings.statusMask);
            listing.CheckboxLabeled("MS_SettingStatusGlow".Translate(),
                ref Settings.statusGlow);
            listing.CheckboxLabeled("MS_SettingIdleMask".Translate(),
                ref Settings.idleMask);
            listing.CheckboxLabeled("MS_SettingIdleGlow".Translate(),
                ref Settings.idleGlow);

            listing.Gap();
            listing.CheckboxLabeled("MS_SettingMechClassOverrides".Translate(),
                ref Settings.applyMechClassOverrides,
                "MS_SettingMechClassOverridesDesc".Translate());

            DrawOptionalOverrides(listing);

            listing.End();
        }

        private const float OverrideIndent = 24f;

        // The entries that brought their own checkbox, two to a row under the
        // main switch. Hidden while that switch is off - a box that cannot do
        // anything is worse than no box at all.
        //
        // Nothing here knows which mods exist. An entry is only in the list
        // because its MayRequire let it load, so the boxes appear and vanish
        // with the mods on their own.
        private static void DrawOptionalOverrides(Listing_Standard listing)
        {
            if (!Settings.applyMechClassOverrides) return;

            List<string> labels = MSMechClassOverrides.ToggleLabelKeys().ToList();
            if (labels.Count == 0) return;

            listing.Gap(6f);

            Rect header = listing.GetRect(Text.LineHeight);
            header.xMin += OverrideIndent;
            Widgets.Label(header, "MS_SettingClassOverridesAlsoApplyTo".Translate());

            for (int i = 0; i < labels.Count; i += 2)
            {
                Rect row = listing.GetRect(Text.LineHeight);
                row.xMin += OverrideIndent;

                DrawOverrideCheckbox(labels[i], row.LeftHalf());
                if (i + 1 < labels.Count)
                    DrawOverrideCheckbox(labels[i + 1], row.RightHalf());
            }
        }

        private static void DrawOverrideCheckbox(string labelKey, Rect rect)
        {
            bool on = Settings.ToggleEnabled(labelKey);
            bool before = on;

            // placeCheckboxNearText, because at half width the default layout
            // would strand the box against the far edge of its column.
            Widgets.CheckboxLabeled(rect, labelKey.Translate(), ref on,
                placeCheckboxNearText: true);

            if (on != before) Settings.SetToggleEnabled(labelKey, on);
        }

        public override string SettingsCategory() => "Mech Stations";
    }
}
