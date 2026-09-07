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

        /// <summary>
        /// Keep re-checking the safe standoff during the approach instead of committing once. When the
        /// prey advances or a herd gathers while the hunter is walking in, it re-picks a safe cell; and
        /// it will cancel a shot lined up from inside the risk cap so the hunter steps back out first.
        /// </summary>
        public bool recheckDuringApproach = true;

        /// <summary>
        /// When a hunter breaks off because the prey closed inside the safety distance, pause the game and
        /// flag the pawn, so the player can take over and micro. Only meaningful with the re-check on.
        /// </summary>
        public bool pauseOnBreakOff = true;

        /// <summary>
        /// Before firing, check what is in the line of fire beyond the prey. The hunter will never shoot
        /// with a friendly pawn behind the target, and prefers not to fire toward the colony's own
        /// buildings. If the prey moves so a friendly ends up behind it, the hunter repositions; if no
        /// safe angle exists it cancels the hunt.
        /// </summary>
        public bool checkShotBackdrop = true;

        /// <summary>How far past the prey the backdrop scan reaches, in cells. The scan stops at the first wall.</summary>
        public float backdropCheckRange = 20f;

        /// <summary>
        /// Also treat the colony's own buildings as something to fire clear of. Buildings are only avoided
        /// when a cleaner angle exists - unlike a friendly pawn, they never cancel a shot.
        /// </summary>
        public bool avoidBuildingBackdrop = true;

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
            Scribe_Values.Look(ref recheckDuringApproach, "recheckDuringApproach", true);
            Scribe_Values.Look(ref pauseOnBreakOff, "pauseOnBreakOff", true);
            Scribe_Values.Look(ref checkShotBackdrop, "checkShotBackdrop", true);
            Scribe_Values.Look(ref backdropCheckRange, "backdropCheckRange", 20f);
            Scribe_Values.Look(ref avoidBuildingBackdrop, "avoidBuildingBackdrop", true);
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

            list.CheckboxLabeled("Re-check the standoff during the approach", ref Settings.recheckDuringApproach,
                "Keep verifying the safe distance while the hunter walks in, instead of committing to the "
                + "spot chosen at the start. If the prey drifts toward that spot, the hunter re-routes to a "
                + "fresh safe cell. If the prey closes the distance once the hunter is in position - the "
                + "rare case of a predator turning to hunt the colonist - the hunter breaks off rather than "
                + "shooting from inside the safety distance.");

            if (Settings.recheckDuringApproach)
            {
                // Sub-option of the re-check above; only relevant when a break-off can happen.
                list.CheckboxLabeled("      Pause and flag the pawn on break-off", ref Settings.pauseOnBreakOff,
                    "When a hunter breaks off because the prey has closed inside the safety distance, pause "
                    + "the game and jump a message to the pawn, so you can step in and micro. The game only "
                    + "pauses on its own once an animal escalates to a full predator-hunt of the colonist; "
                    + "this catches the earlier moment the hunter decides to disengage.");
            }

            list.GapLine();

            list.CheckboxLabeled("Check the shot's backdrop", ref Settings.checkShotBackdrop,
                "Before firing, the hunter looks at what is behind the prey in the line of fire. It will "
                + "never take a shot with a colonist, pet, or other friendly pawn downrange - if the prey "
                + "drifts so a friendly ends up behind it, the hunter shifts to a safe angle, and if no "
                + "safe angle exists it cancels the hunt and tells you why.");

            if (Settings.checkShotBackdrop)
            {
                LabelWithTip(list, $"      Backdrop scan distance: {Settings.backdropCheckRange:F0} cells",
                    "How far past the prey to look for friendlies and buildings a stray shot could reach. "
                    + "The scan always stops at the first wall, which safely absorbs the shot.");
                Settings.backdropCheckRange = list.Slider(Settings.backdropCheckRange, 5f, 40f);

                list.CheckboxLabeled("      Also keep the shot clear of your buildings", ref Settings.avoidBuildingBackdrop,
                    "Prefer firing angles with no player-built structure behind the prey. Buildings are only "
                    + "avoided when a cleaner angle is available - unlike a friendly pawn, they never cancel "
                    + "a shot.");
            }

            list.GapLine();

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
