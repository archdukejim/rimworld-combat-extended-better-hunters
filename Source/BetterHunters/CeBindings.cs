using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BetterHunters
{
    /// <summary>
    /// Every point of contact with Combat Extended lives here, resolved by reflection at startup.
    ///
    /// Why reflection instead of a compile-time reference to CombatExtended.dll:
    ///   * CE's surface is version-specific. Verified against CE Development/master @ modVersion
    ///     16.7.3.0 (RimWorld 1.6). CE's 1.5-era source does not even contain CE_Math.cs, so the
    ///     preferred solver simply does not exist there.
    ///   * Reflection lets a missing member degrade to a documented fallback (or a clean self-disable)
    ///     instead of a TypeLoadException that takes the whole mod list down.
    ///
    /// CE types touched (all cited at their use sites):
    ///   CombatExtended.Verb_LaunchProjectileCE   - Source/CombatExtended/CombatExtended/Verbs/Verb_LaunchProjectileCE.cs
    ///   CombatExtended.CE_Math                   - Source/CombatExtended/CombatExtended/CE_Math.cs
    ///   CombatExtended.CE_Utility                - Source/CombatExtended/CombatExtended/CE_Utility.cs
    ///   CombatExtended.CE_StatDefOf              - Source/CombatExtended/CombatExtended/DefOfs/CE_StatDefOf.cs
    ///   CombatExtended.BipodComp                 - Source/CombatExtended/CombatExtended/Comps/BipodComp.cs
    ///   CombatExtended.CompProperties_BipodComp  - Source/CombatExtended/CombatExtended/Comps/CompProperties_BipodComp.cs
    ///   CombatExtended.CE_JobDefOf               - Source/CombatExtended/CombatExtended/DefOfs/CE_JobDefOf.cs
    /// </summary>
    [StaticConstructorOnStartup]
    public static class CeBindings
    {
        // ---- CE types -------------------------------------------------------------------------
        public static readonly Type VerbLaunchProjectileCeType;
        public static readonly Type BipodCompType;

        // ---- CE_Math --------------------------------------------------------------------------
        // float CalculateMaxDistance(float threshold, Bounds bounds, float shotSpeed, float swayAmplitude,
        //                            float spreadDegrees, float sightsEfficiency, float aimingAccuracy, float gravity)
        // Returns the greatest distance at which first-shot hit probability is still >= threshold.
        private static readonly MethodInfo CalculateMaxDistanceMi;

        // float CalculateHitPercent(float dist, Bounds bounds, float offset, float shotSpeed, float shotAngle,
        //                           float swayDegrees, float spreadDegrees, float visibilityShift, float gravity)
        // Used only as the fallback when CalculateMaxDistance is unavailable.
        private static readonly MethodInfo CalculateHitPercentMi;

        // ---- CE_Utility -----------------------------------------------------------------------
        private static readonly MethodInfo GetBoundsForMi;   // Bounds GetBoundsFor(Thing)
        public static readonly float GravityConst = 9.8f;    // CE_Utility.GravityConst; literal is the CE default

        // ---- Verb_LaunchProjectileCE members --------------------------------------------------
        private static readonly MethodInfo AimingAccuracyGetter;    // public float AimingAccuracy
        private static readonly MethodInfo SightsEfficiencyGetter;  // public float SightsEfficiency
        private static readonly MethodInfo SwayAmplitudeGetter;     // public virtual float SwayAmplitude
        private static readonly MethodInfo ProjectileGetter;        // public virtual ThingDef Projectile (ammo-aware)
        private static readonly MethodInfo ProjectilePropsCeGetter; // public ProjectilePropertiesCE projectilePropsCE
        private static readonly MethodInfo ShotSpeedGetter;         // protected float ShotSpeed
        private static readonly FieldInfo SpreadMultField;          // ProjectilePropertiesCE.spreadMult

        // ---- Def-backed bindings (resolved lazily, see note below) ----------------------------
        // CE_StatDefOf.ShotSpread and CE_JobDefOf.JobDef_SetUpBipod are DefOf fields. DefOf classes are
        // not populated until DefOfHelper.RebindAllDefOfs runs, which happens AFTER every Mod
        // constructor. This class is first touched from our Mod constructor, so reading these in the
        // static constructor would capture null and permanently disable the features that need them.
        // They are therefore resolved on first use, by which point defs are loaded.
        private static FieldInfo shotSpreadStatField;   // CE_StatDefOf.ShotSpread
        private static FieldInfo setUpBipodJobField;    // CE_JobDefOf.JobDef_SetUpBipod
        private static StatDef shotSpreadStat;
        private static JobDef setUpBipodJobDef;
        private static bool shotSpreadResolved;
        private static bool bipodJobResolved;

        // ---- Bipod ----------------------------------------------------------------------------
        private static readonly FieldInfo BipodIsSetUpField;   // public bool IsSetUpRn
        private static readonly MethodInfo BipodDeployMethod;  // public void DeployUpBipod()
        private static readonly FieldInfo BipodAddlRangeField; // CompProperties_BipodComp.additionalrange

        /// <summary>True when enough of CE was found to compute an engagement range.</summary>
        public static bool CoreAvailable { get; private set; }

        /// <summary>True when the CE-authored threshold-&gt;distance solver is present (CE 1.6+).</summary>
        public static bool HasNativeSolver => CalculateMaxDistanceMi != null;

        /// <summary>
        /// True when CE's bipod comp resolved. Safe to check from the Mod constructor - deliberately
        /// does NOT test the setup JobDef, which is not loaded that early; that is checked at use time.
        /// </summary>
        public static bool BipodAvailable => BipodCompType != null && BipodDeployMethod != null
                                             && BipodIsSetUpField != null;

        static CeBindings()
        {
            try
            {
                VerbLaunchProjectileCeType = AccessTools.TypeByName("CombatExtended.Verb_LaunchProjectileCE");
                if (VerbLaunchProjectileCeType == null)
                {
                    Log.Error("[BetterHunters] Combat Extended not found (CombatExtended.Verb_LaunchProjectileCE is missing). "
                              + "This mod has a hard dependency on CE and will stay inactive.");
                    return;
                }

                Type ceMath = AccessTools.TypeByName("CombatExtended.CE_Math");
                Type ceUtility = AccessTools.TypeByName("CombatExtended.CE_Utility");

                // CE_Math.CalculateMaxDistance - the Bounds overload. Verified in CE_Math.cs:136.
                if (ceMath != null)
                {
                    CalculateMaxDistanceMi = AccessTools.Method(ceMath, "CalculateMaxDistance", new[]
                    {
                        typeof(float), typeof(Bounds), typeof(float), typeof(float),
                        typeof(float), typeof(float), typeof(float), typeof(float)
                    });

                    // CE_Math.CalculateHitPercent, Bounds overload. Verified in CE_Math.cs:83.
                    CalculateHitPercentMi = AccessTools.Method(ceMath, "CalculateHitPercent", new[]
                    {
                        typeof(float), typeof(Bounds), typeof(float), typeof(float), typeof(float),
                        typeof(float), typeof(float), typeof(float), typeof(float)
                    });
                }

                // CE_Utility.GetBoundsFor(Thing). Verified in CE_Utility.cs:967.
                if (ceUtility != null)
                {
                    GetBoundsForMi = AccessTools.Method(ceUtility, "GetBoundsFor", new[] { typeof(Thing) });

                    FieldInfo gravity = AccessTools.Field(ceUtility, "GravityConst");
                    if (gravity != null && gravity.IsLiteral)
                    {
                        GravityConst = (float)gravity.GetRawConstantValue();
                    }
                }

                AimingAccuracyGetter = AccessTools.PropertyGetter(VerbLaunchProjectileCeType, "AimingAccuracy");
                SightsEfficiencyGetter = AccessTools.PropertyGetter(VerbLaunchProjectileCeType, "SightsEfficiency");
                SwayAmplitudeGetter = AccessTools.PropertyGetter(VerbLaunchProjectileCeType, "SwayAmplitude");
                ProjectileGetter = AccessTools.PropertyGetter(VerbLaunchProjectileCeType, "Projectile");
                ProjectilePropsCeGetter = AccessTools.PropertyGetter(VerbLaunchProjectileCeType, "projectilePropsCE");
                // ShotSpeed is `protected`; reflection reaches it and it handles CompCharges brackets
                // that a raw Projectile.projectile.speed read would miss.
                ShotSpeedGetter = AccessTools.PropertyGetter(VerbLaunchProjectileCeType, "ShotSpeed");

                Type projPropsCe = AccessTools.TypeByName("CombatExtended.ProjectilePropertiesCE");
                if (projPropsCe != null)
                {
                    SpreadMultField = AccessTools.Field(projPropsCe, "spreadMult");
                }

                // CE_StatDefOf.ShotSpread - grab the FieldInfo now, read its value lazily (see above).
                Type ceStatDefOf = AccessTools.TypeByName("CombatExtended.CE_StatDefOf");
                if (ceStatDefOf != null)
                {
                    shotSpreadStatField = AccessTools.Field(ceStatDefOf, "ShotSpread");
                }

                // ---- Bipod ------------------------------------------------------------------
                BipodCompType = AccessTools.TypeByName("CombatExtended.BipodComp");
                if (BipodCompType != null)
                {
                    BipodIsSetUpField = AccessTools.Field(BipodCompType, "IsSetUpRn");
                    BipodDeployMethod = AccessTools.Method(BipodCompType, "DeployUpBipod");
                }

                Type bipodProps = AccessTools.TypeByName("CombatExtended.CompProperties_BipodComp");
                if (bipodProps != null)
                {
                    BipodAddlRangeField = AccessTools.Field(bipodProps, "additionalrange");
                }

                Type ceJobDefOf = AccessTools.TypeByName("CombatExtended.CE_JobDefOf");
                if (ceJobDefOf != null)
                {
                    setUpBipodJobField = AccessTools.Field(ceJobDefOf, "JobDef_SetUpBipod");
                }

                CoreAvailable = GetBoundsForMi != null
                                && AimingAccuracyGetter != null
                                && SightsEfficiencyGetter != null
                                && SwayAmplitudeGetter != null
                                && (CalculateMaxDistanceMi != null || CalculateHitPercentMi != null);

                if (!CoreAvailable)
                {
                    Log.Warning("[BetterHunters] Combat Extended is present but its hit-chance API did not resolve "
                                + "(this is expected on CE builds older than the 1.6 line, which ship no CE_Math). "
                                + "Hunter repositioning is disabled; the rest of the game is unaffected.");
                }
                else if (!HasNativeSolver)
                {
                    Log.Message("[BetterHunters] CE_Math.CalculateMaxDistance not found; using a local binary search "
                                + "over CE_Math.CalculateHitPercent instead. Results may differ slightly from CE's own.");
                }
            }
            catch (Exception ex)
            {
                CoreAvailable = false;
                Log.Error("[BetterHunters] Failed to bind to Combat Extended; staying inactive. " + ex);
            }
        }

        /// <summary>True if this verb is a CE ranged projectile verb (i.e. not melee, not vanilla).</summary>
        public static bool IsCeRangedVerb(Verb verb)
        {
            return verb != null
                   && VerbLaunchProjectileCeType != null
                   && VerbLaunchProjectileCeType.IsInstanceOfType(verb);
        }

        /// <summary>CE_Utility.GetBoundsFor - the target silhouette CE's own hit math uses.</summary>
        public static bool TryGetBounds(Thing thing, out Bounds bounds)
        {
            bounds = default(Bounds);
            if (GetBoundsForMi == null || thing == null)
            {
                return false;
            }

            try
            {
                bounds = (Bounds)GetBoundsForMi.Invoke(null, new object[] { thing });
                return true;
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[BetterHunters] CE_Utility.GetBoundsFor threw: " + ex.Message, 0x5BE7A1);
                return false;
            }
        }

        /// <summary>
        /// Pulls the CE aiming parameters off a live verb. These are exactly the inputs CE feeds into
        /// its own ShiftVecReport (Verb_LaunchProjectileCE.ShiftVecReportFor, Verb_LaunchProjectileCE.cs:655).
        /// </summary>
        public static bool TryGetAimParams(Verb verb, out CeAimParams p)
        {
            p = default(CeAimParams);
            if (!IsCeRangedVerb(verb))
            {
                return false;
            }

            try
            {
                p.aimingAccuracy = (float)AimingAccuracyGetter.Invoke(verb, null);
                p.sightsEfficiency = (float)SightsEfficiencyGetter.Invoke(verb, null);
                p.swayAmplitude = (float)SwayAmplitudeGetter.Invoke(verb, null);

                // spreadDegrees mirrors ShiftVecReportFor:681 -
                //   (EquipmentSource?.GetStatValue(CE_StatDefOf.ShotSpread) ?? 0) * projectilePropsCE.spreadMult
                float spreadMult = 0f;
                if (ProjectilePropsCeGetter != null && SpreadMultField != null)
                {
                    object props = ProjectilePropsCeGetter.Invoke(verb, null);
                    if (props != null)
                    {
                        spreadMult = (float)SpreadMultField.GetValue(props);
                    }
                }

                float shotSpread = 0f;
                StatDef spreadStat = ShotSpreadStat;
                if (spreadStat != null && verb.EquipmentSource != null)
                {
                    shotSpread = verb.EquipmentSource.GetStatValue(spreadStat);
                }

                p.spreadDegrees = shotSpread * spreadMult;
                p.shotSpeed = GetShotSpeed(verb);
                p.projectile = GetProjectileDef(verb);

                return p.shotSpeed > 0f;
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[BetterHunters] Reading CE aim parameters failed: " + ex.Message, 0x5BE7A2);
                return false;
            }
        }

        /// <summary>
        /// Verb_LaunchProjectileCE.Projectile - ammo-aware, so this is the projectile CE will actually
        /// launch given the currently loaded magazine, not the weapon def's default.
        /// </summary>
        public static ThingDef GetProjectileDef(Verb verb)
        {
            if (ProjectileGetter == null || !IsCeRangedVerb(verb))
            {
                return null;
            }

            try
            {
                return ProjectileGetter.Invoke(verb, null) as ThingDef;
            }
            catch
            {
                return null;
            }
        }

        private static float GetShotSpeed(Verb verb)
        {
            if (ShotSpeedGetter != null)
            {
                try
                {
                    return (float)ShotSpeedGetter.Invoke(verb, null);
                }
                catch
                {
                    // ShotSpeed touches currentTarget/caster and can throw when the verb is idle.
                    // Fall through to the projectile def's own speed.
                }
            }

            ThingDef proj = GetProjectileDef(verb);
            return proj?.projectile?.speed ?? 0f;
        }

        /// <summary>
        /// CE_Math.CalculateMaxDistance - greatest range whose first-shot hit chance still meets
        /// <paramref name="threshold"/>. Because hit chance falls off monotonically with distance, this
        /// value IS the easy-shot band's outer edge: anywhere at or inside it is an "easy" shot.
        /// Falls back to a binary search over CE_Math.CalculateHitPercent when CE predates the solver.
        /// </summary>
        public static bool TryCalculateEasyShotRange(float threshold, Bounds bounds, CeAimParams p, out float range)
        {
            range = 0f;

            if (CalculateMaxDistanceMi != null)
            {
                try
                {
                    range = (float)CalculateMaxDistanceMi.Invoke(null, new object[]
                    {
                        threshold, bounds, p.shotSpeed, p.swayAmplitude,
                        p.spreadDegrees, p.sightsEfficiency, p.aimingAccuracy, GravityConst
                    });
                    return range > 0f && !float.IsNaN(range) && !float.IsInfinity(range);
                }
                catch (Exception ex)
                {
                    Log.WarningOnce("[BetterHunters] CE_Math.CalculateMaxDistance threw: " + ex.Message, 0x5BE7A3);
                }
            }

            return TryBinarySearchEasyRange(threshold, bounds, p, out range);
        }

        /// <summary>
        /// Fallback solver. Mirrors the shape of CE's own CalculateMaxDistance (CE_Math.cs:143): probe
        /// distances with CalculateHitPercent and bisect on the threshold. Slightly less faithful than
        /// CE's version because we do not reproduce its baked-in visibility constant exactly.
        /// FLAG: only exercised on CE builds without CalculateMaxDistance; unverified against CE 1.5.
        /// </summary>
        private static bool TryBinarySearchEasyRange(float threshold, Bounds bounds, CeAimParams p, out float range)
        {
            range = 0f;
            if (CalculateHitPercentMi == null)
            {
                return false;
            }

            // Same reverse-extracted visibility constant CE uses in CalculateMaxDistance (CE_Math.cs:161).
            float se = Mathf.Max(p.sightsEfficiency, 0.02f);
            float visibility = 0.07f * (2f - p.aimingAccuracy) / se;

            float lo = 1f;
            float hi = 200f;

            try
            {
                for (int i = 0; i < 14; i++)
                {
                    float mid = (lo + hi) * 0.5f;
                    float hit = (float)CalculateHitPercentMi.Invoke(null, new object[]
                    {
                        mid, bounds, 0f, p.shotSpeed, 0f,
                        p.swayAmplitude, p.spreadDegrees, visibility * mid, GravityConst
                    });

                    if (hit >= threshold)
                    {
                        lo = mid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[BetterHunters] CE_Math.CalculateHitPercent threw: " + ex.Message, 0x5BE7A4);
                return false;
            }

            range = lo;
            return range > 1f;
        }

        // ---- Bipod ---------------------------------------------------------------------------

        /// <summary>Returns the weapon's CombatExtended.BipodComp, or null.</summary>
        public static ThingComp GetBipodComp(ThingWithComps weapon)
        {
            if (weapon?.AllComps == null || BipodCompType == null)
            {
                return null;
            }

            for (int i = 0; i < weapon.AllComps.Count; i++)
            {
                ThingComp comp = weapon.AllComps[i];
                if (BipodCompType.IsInstanceOfType(comp))
                {
                    return comp;
                }
            }

            return null;
        }

        /// <summary>BipodComp.IsSetUpRn - true once the bipod is actually deployed.</summary>
        public static bool IsBipodDeployed(ThingComp bipod)
        {
            if (bipod == null || BipodIsSetUpField == null)
            {
                return false;
            }

            try
            {
                return (bool)BipodIsSetUpField.GetValue(bipod);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// BipodComp.DeployUpBipod() - sets the comp's ShouldSetUpint flag. On its own this does NOT
        /// deploy anything; CE finishes the job in JobDriver_SetUpBipod, which calls BipodComp.SetUpEnd.
        /// We therefore set the flag AND start the job ourselves (see BipodDeployPatch).
        /// </summary>
        public static void RequestBipodDeploy(ThingComp bipod)
        {
            if (bipod == null || BipodDeployMethod == null)
            {
                return;
            }

            try
            {
                BipodDeployMethod.Invoke(bipod, null);
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[BetterHunters] BipodComp.DeployUpBipod threw: " + ex.Message, 0x5BE7A5);
            }
        }

        /// <summary>
        /// CE_StatDefOf.ShotSpread. Resolved on first access rather than in the static constructor,
        /// because DefOf fields are still null while Mod constructors run.
        /// </summary>
        private static StatDef ShotSpreadStat
        {
            get
            {
                if (!shotSpreadResolved && shotSpreadStatField != null)
                {
                    try
                    {
                        shotSpreadStat = shotSpreadStatField.GetValue(null) as StatDef;
                    }
                    catch
                    {
                        shotSpreadStat = null;
                    }

                    // Only latch once a value actually arrives, so an early probe cannot poison it.
                    shotSpreadResolved = shotSpreadStat != null;
                }

                return shotSpreadStat;
            }
        }

        /// <summary>
        /// CE_JobDefOf.JobDef_SetUpBipod - the job whose driver (CombatExtended.JobDriver_SetUpBipod)
        /// waits ticksToSetUp and then calls BipodComp.SetUpEnd. Lazily resolved, same reason as above.
        /// </summary>
        public static JobDef BipodSetupJob
        {
            get
            {
                if (!bipodJobResolved && setUpBipodJobField != null)
                {
                    try
                    {
                        setUpBipodJobDef = setUpBipodJobField.GetValue(null) as JobDef;
                    }
                    catch
                    {
                        setUpBipodJobDef = null;
                    }

                    bipodJobResolved = setUpBipodJobDef != null;
                }

                return setUpBipodJobDef;
            }
        }

        /// <summary>
        /// CompProperties_BipodComp.additionalrange - extra weapon range granted once deployed
        /// (BipodComp.SetUpEnd, BipodComp.cs:167). Used only to keep the engagement range inside the
        /// range the weapon will have *after* deployment.
        /// </summary>
        public static float GetBipodAdditionalRange(ThingComp bipod)
        {
            if (bipod == null || BipodAddlRangeField == null)
            {
                return 0f;
            }

            try
            {
                object props = bipod.props;
                return props == null ? 0f : Convert.ToSingle(BipodAddlRangeField.GetValue(props));
            }
            catch
            {
                return 0f;
            }
        }
    }

    /// <summary>The CE aiming inputs needed to evaluate a shot, snapshotted off a live verb.</summary>
    public struct CeAimParams
    {
        public float aimingAccuracy;
        public float sightsEfficiency;
        public float swayAmplitude;
        public float spreadDegrees;
        public float shotSpeed;
        public ThingDef projectile;
    }
}
