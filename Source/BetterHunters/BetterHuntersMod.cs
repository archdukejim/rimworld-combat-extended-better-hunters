using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace BetterHunters
{
    public class BetterHuntersSettings : ModSettings
    {
        /// <summary>Master on/off. When false every patch short-circuits to vanilla CE behaviour.</summary>
        public bool enabled = true;

        /// <summary>
        /// "Easy shot" threshold: the first-shot hit probability the hunter wants before firing.
        /// Fed straight into CE_Math.CalculateMaxDistance as its `threshold` argument.
        /// </summary>
        public float easyShotHitChance = 0.80f;

        /// <summary>
        /// Highest RaceProperties.manhunterOnDamageChance the hunter will tolerate at point blank.
        /// Anything above this starts pushing the standoff distance out.
        /// </summary>
        public float maxRevengeChance = 0.10f;

        /// <summary>Cells of standoff added per nearby herd-mate, before the danger multiplier.</summary>
        public float herdSensitivity = 1.0f;

        /// <summary>Radius (cells) scanned around the prey for conspecifics.</summary>
        public float herdScanRadius = 12f;

        /// <summary>Cells of standoff per 1.0 of revenge chance above the tolerance, before danger scaling.</summary>
        public float revengeDistanceScale = 30f;

        /// <summary>Hard ceiling on the computed risk cap, so a thrumbo cannot push it past sanity.</summary>
        public float maxRiskCapCells = 40f;

        /// <summary>
        /// When the easy-shot range is inside the risk cap, allow closing anyway if the shot is likely
        /// to drop the animal outright. A dead animal takes no revenge.
        /// </summary>
        public bool closeForOneShotKill = true;

        /// <summary>Deploy a CE bipod, if present, before taking the shot.</summary>
        public bool deployBipod = true;

        public bool debugLogging = false;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref easyShotHitChance, "easyShotHitChance", 0.80f);
            Scribe_Values.Look(ref maxRevengeChance, "maxRevengeChance", 0.10f);
            Scribe_Values.Look(ref herdSensitivity, "herdSensitivity", 1.0f);
            Scribe_Values.Look(ref herdScanRadius, "herdScanRadius", 12f);
            Scribe_Values.Look(ref revengeDistanceScale, "revengeDistanceScale", 30f);
            Scribe_Values.Look(ref maxRiskCapCells, "maxRiskCapCells", 40f);
            Scribe_Values.Look(ref closeForOneShotKill, "closeForOneShotKill", true);
            Scribe_Values.Look(ref deployBipod, "deployBipod", true);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
        }
    }

    public class BetterHuntersMod : Mod
    {
        public static BetterHuntersSettings Settings { get; private set; }

        public BetterHuntersMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<BetterHuntersSettings>();

            Harmony harmony = new Harmony("archdukejim.betterhunters");
            harmony.PatchAll(Assembly.GetExecutingAssembly());

            // The bipod hook targets a CE type, so it cannot be an attribute patch - it is wired
            // manually once CE has been resolved by reflection.
            BipodDeployPatch.TryApply(harmony);
        }

        public override string SettingsCategory() => "Better Hunters (CE)";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard list = new Listing_Standard();
            list.Begin(inRect);

            list.CheckboxLabeled("Enable smart hunter positioning", ref Settings.enabled,
                "Master switch. When off, hunters behave exactly as they do under stock Combat Extended.");

            list.GapLine();

            LabelWithTip(list, $"Easy-shot hit chance: {Settings.easyShotHitChance:P0}",
                "The hunter walks until Combat Extended's own hit-chance math says the first shot "
                + "is at least this likely to land. Higher means closing further before firing.");
            Settings.easyShotHitChance = list.Slider(Settings.easyShotHitChance, 0.30f, 0.99f);

            LabelWithTip(list, $"Tolerated revenge chance: {Settings.maxRevengeChance:P0}",
                "Animals whose manhunter-on-damage chance is at or below this are approached freely. "
                + "Above it, the hunter keeps its distance.");
            Settings.maxRevengeChance = list.Slider(Settings.maxRevengeChance, 0f, 1f);

            LabelWithTip(list, $"Revenge standoff scale: {Settings.revengeDistanceScale:F0} cells",
                "Cells of extra standoff per full point of revenge chance above the tolerance, "
                + "before size/predator scaling.");
            Settings.revengeDistanceScale = list.Slider(Settings.revengeDistanceScale, 0f, 80f);

            LabelWithTip(list, $"Herd sensitivity: {Settings.herdSensitivity:F2} cells per herd-mate",
                "Extra standoff for each nearby animal of the same species that could join in.");
            Settings.herdSensitivity = list.Slider(Settings.herdSensitivity, 0f, 5f);

            LabelWithTip(list, $"Herd scan radius: {Settings.herdScanRadius:F0} cells",
                "How far around the prey to look for herd-mates.");
            Settings.herdScanRadius = list.Slider(Settings.herdScanRadius, 0f, 30f);

            LabelWithTip(list, $"Maximum standoff: {Settings.maxRiskCapCells:F0} cells",
                "Hard ceiling on the computed safety distance.");
            Settings.maxRiskCapCells = list.Slider(Settings.maxRiskCapCells, 0f, 80f);

            list.GapLine();

            list.CheckboxLabeled("Close in when the shot should drop it outright", ref Settings.closeForOneShotKill,
                "If no easy shot exists outside the safety distance, still close in when the projectile "
                + "should take the animal down in one hit.");

            list.CheckboxLabeled("Deploy CE bipod before shooting", ref Settings.deployBipod,
                "If the hunting weapon has a Combat Extended bipod, set it up at the firing position "
                + "before taking the shot.");

            list.CheckboxLabeled("Debug logging", ref Settings.debugLogging,
                "Logs the computed easy-shot range, risk cap and final engagement range for each hunt.");

            list.End();
            base.DoSettingsWindowContents(inRect);
        }

        /// <summary>
        /// Listing_Standard.Label has overloads taking (TaggedString, float, string) and
        /// (string, float, TipSignal?), so a bare string tooltip is ambiguous. Wrapping the tooltip in
        /// an explicit TipSignal picks the intended overload.
        /// </summary>
        private static void LabelWithTip(Listing_Standard list, string text, string tip)
        {
            list.Label(text, -1f, new TipSignal(tip));
        }
    }
}
