using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace BetterHunters
{
    /// <summary>
    /// Deploys a Combat Extended bipod before the hunter takes its shot.
    ///
    /// Why this is needed at all: CE only ever sets a bipod up by itself from
    /// CombatExtended.BipodComp.SetUpStart, which opens with
    ///     if (!(pawn?.Drafted ?? false)) return;
    /// and CompGetGizmosExtra is likewise gated on Drafted. A hunter on the Hunt work job is never
    /// drafted, so stock CE will not deploy a bipod for a hunter under any setting. We start CE's own
    /// setup job ourselves instead of reimplementing the deployment.
    ///
    /// Hook point: CombatExtended.Verb_LaunchProjectileCE.TryStartCastOn(...), the override CE uses to
    /// begin a shot (Verb_LaunchProjectileCE.cs:1030). Patched manually rather than by attribute,
    /// because the declaring type only exists when CE is loaded.
    ///
    /// FLAG - needs in-game verification: the deploy path is CE-internal and version-sensitive. The
    /// sequence used here is DeployUpBipod() to raise BipodComp.ShouldSetUpint, then CE's
    /// CE_JobDefOf.JobDef_SetUpBipod job, whose driver (CombatExtended.JobDriver_SetUpBipod) waits
    /// CompProperties_BipodComp.ticksToSetUp and then calls BipodComp.SetUpEnd. All names verified
    /// against CE modVersion 16.7.3.0 source; runtime behaviour with a resumed work job is not.
    /// </summary>
    public static class BipodDeployPatch
    {
        private const int RetryCooldownTicks = 600;
        private const int MaxAttemptsPerPawn = 3;

        private struct Attempt
        {
            public int lastTick;
            public int count;
        }

        private static readonly Dictionary<int, Attempt> Attempts = new Dictionary<int, Attempt>();

        /// <summary>Wires the patch onto CE's verb, if CE is present. Called from the Mod constructor.</summary>
        public static void TryApply(Harmony harmony)
        {
            if (CeBindings.VerbLaunchProjectileCeType == null)
            {
                return;
            }

            if (!CeBindings.BipodAvailable)
            {
                Log.Warning("[BetterHunters] Combat Extended is loaded but CombatExtended.BipodComp did not "
                            + "resolve. Bipod deployment is disabled; hunter positioning is unaffected.");
                return;
            }

            try
            {
                MethodInfo target = AccessTools.Method(
                    CeBindings.VerbLaunchProjectileCeType,
                    "TryStartCastOn",
                    new[]
                    {
                        typeof(LocalTargetInfo), typeof(LocalTargetInfo),
                        typeof(bool), typeof(bool), typeof(bool), typeof(bool)
                    });

                if (target == null)
                {
                    Log.Warning("[BetterHunters] Could not find Verb_LaunchProjectileCE.TryStartCastOn "
                                + "with the expected signature; bipod deployment is disabled.");
                    return;
                }

                harmony.Patch(target, prefix: new HarmonyMethod(typeof(BipodDeployPatch), nameof(Prefix)));
            }
            catch (Exception ex)
            {
                Log.Error("[BetterHunters] Failed to patch Verb_LaunchProjectileCE.TryStartCastOn: " + ex);
            }
        }

        /// <summary>
        /// Runs immediately before CE starts a shot. If the shooter is a hunter standing at its firing
        /// position with an undeployed bipod, we suppress the shot, deploy, and let the hunt job resume.
        /// </summary>
        public static bool Prefix(Verb __instance, ref bool __result)
        {
            BetterHuntersSettings s = BetterHuntersMod.Settings;
            if (s == null || !s.enabled || !s.deployBipod || !CeBindings.BipodAvailable)
            {
                return true;
            }

            try
            {
                Pawn pawn = __instance?.CasterPawn;
                if (pawn?.jobs == null || pawn.Map == null)
                {
                    return true;
                }

                // Only while hunting - drafted combat keeps CE's own (gizmo-driven) bipod handling.
                if (pawn.CurJob == null || pawn.CurJob.def != JobDefOf.Hunt)
                {
                    return true;
                }

                // Deploy at the firing position, not mid-walk.
                if (pawn.pather != null && pawn.pather.MovingNow)
                {
                    return true;
                }

                ThingWithComps weapon = pawn.equipment?.Primary;
                if (weapon == null)
                {
                    return true;
                }

                ThingComp bipod = CeBindings.GetBipodComp(weapon);
                if (bipod == null || CeBindings.IsBipodDeployed(bipod))
                {
                    return true;
                }

                // Checked here rather than at patch time: CE_JobDefOf is a DefOf and is still null
                // while Mod constructors run.
                JobDef setupJobDef = CeBindings.BipodSetupJob;
                if (setupJobDef == null)
                {
                    return true;
                }

                if (!ShouldAttempt(pawn))
                {
                    return true;
                }

                // Raise CE's own intent flag, then run CE's setup job. resumeCurJobAfterwards puts the
                // hunt job back on the stack so the pawn returns to shooting once the bipod is up.
                CeBindings.RequestBipodDeploy(bipod);

                Job setup = JobMaker.MakeJob(setupJobDef, weapon);
                pawn.jobs.StartJob(setup, JobCondition.InterruptForced, null, resumeCurJobAfterwards: true);

                if (s.debugLogging)
                {
                    Log.Message($"[BetterHunters] {pawn.LabelShort} deploying bipod on {weapon.LabelShort} "
                                + "before shooting.");
                }

                // Suppress this cast; the hunt job resumes and casts again with the bipod deployed.
                __result = false;
                return false;
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[BetterHunters] bipod prefix failed: " + ex, 0x5BE7C1);
                return true;
            }
        }

        /// <summary>
        /// Backstop against a deploy loop: if the setup job keeps failing (reservation lost, job
        /// interrupted, CE refusing to flip IsSetUpRn) we give up and let the hunter shoot as-is.
        /// </summary>
        private static bool ShouldAttempt(Pawn pawn)
        {
            int now = Find.TickManager.TicksGame;
            int key = pawn.thingIDNumber;

            if (!Attempts.TryGetValue(key, out Attempt a))
            {
                Attempts[key] = new Attempt { lastTick = now, count = 1 };
                return true;
            }

            if (now - a.lastTick > RetryCooldownTicks)
            {
                // Long gap - treat this as a fresh hunt.
                Attempts[key] = new Attempt { lastTick = now, count = 1 };
                return true;
            }

            if (a.count >= MaxAttemptsPerPawn)
            {
                return false;
            }

            Attempts[key] = new Attempt { lastTick = now, count = a.count + 1 };
            return true;
        }

        /// <summary>Clears the retry bookkeeping. Called on game load.</summary>
        public static void Reset() => Attempts.Clear();
    }
}
