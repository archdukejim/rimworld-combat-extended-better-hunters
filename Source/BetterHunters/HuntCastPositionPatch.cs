using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BetterHunters
{
    /// <summary>State handed from the prefix to the postfix for one TryFindCastPosition call.</summary>
    public class HuntEngagement
    {
        public Pawn hunter;
        public Pawn victim;
        public Verb verb;
        public EngagementSolution solution;
        public CastPositionRequest originalRequest;
    }

    /// <summary>
    /// Patches the cast-position decision that RimWorld's hunt job uses.
    ///
    /// Chain being intercepted:
    ///   RimWorld.JobDriver_Hunt.MakeNewToils
    ///     -> Verse.AI.Toils_Combat.GotoCastPosition(TargetIndex, TargetIndex, bool, float maxRangeFactor)
    ///        (JobDriver_Hunt passes its static JobDriver_Hunt.MaxRangeFactor)
    ///          -> Verse.AI.CastPositionFinder.TryFindCastPosition(CastPositionRequest newReq, ref IntVec3 dest)
    ///
    /// We patch the last link. It is the single choke point for "where does the hunter stand", it is
    /// a plain static method rather than a compiler-generated iterator, and Combat Extended does not
    /// patch it (checked against CE's full Harmony patch directory), so there is no conflict.
    ///
    /// Prefix : pulls CastPositionRequest.maxRangeFromTarget in to the computed engagement range, so
    ///          the vanilla scorer stops the hunter at easy-shot distance instead of at weapon range.
    /// Postfix: enforces the *minimum* distance, which CastPositionRequest has no field for, by
    ///          re-picking a cell from the safe annulus when vanilla chose one inside the risk cap.
    /// </summary>
    [HarmonyPatch(typeof(CastPositionFinder), nameof(CastPositionFinder.TryFindCastPosition))]
    public static class HuntCastPositionPatch
    {
        /// <summary>Guards the vanilla-behaviour retry in the postfix from re-entering this patch.</summary>
        [ThreadStatic] private static bool reentrant;

        [HarmonyPriority(Priority.Normal)]
        public static void Prefix(ref CastPositionRequest newReq, out HuntEngagement __state)
        {
            __state = null;

            if (reentrant)
            {
                return;
            }

            BetterHuntersSettings s = BetterHuntersMod.Settings;
            if (s == null || !s.enabled || !CeBindings.CoreAvailable)
            {
                return;
            }

            try
            {
                Pawn hunter = newReq.caster;

                // Only the colonist hunting work job. Predator hunting (JobDefOf.PredatorHunt) and all
                // drafted combat keep vanilla CE behaviour.
                if (hunter?.CurJob == null || hunter.CurJob.def != JobDefOf.Hunt)
                {
                    return;
                }

                if (!(newReq.target is Pawn victim) || victim.Dead)
                {
                    return;
                }

                // Melee hunting (CE allows it via Controller.settings.AllowMeleeHunting) has no ranged
                // solution - leave it alone.
                if (!CeBindings.IsCeRangedVerb(newReq.verb))
                {
                    return;
                }

                if (!EngagementSolver.TrySolve(hunter, victim, newReq.verb, out EngagementSolution sol))
                {
                    return;
                }

                __state = new HuntEngagement
                {
                    hunter = hunter,
                    victim = victim,
                    verb = newReq.verb,
                    solution = sol,
                    originalRequest = newReq
                };

                // Cap the far side. Vanilla's scorer prefers cells near the caster, so with this ceiling
                // in place the hunter walks in only until the shot becomes easy, then stops.
                newReq.maxRangeFromTarget = Mathf.Min(newReq.maxRangeFromTarget, sol.engagementRange);

                // This is the single place that already identifies a managed hunt, so register the hunter
                // for the mid-approach re-check here rather than paying a per-tick patch on every pawn's
                // driver. The watchdog handles both the standoff re-check and the backdrop re-check, so
                // register when either is enabled.
                if (s.recheckDuringApproach || s.checkShotBackdrop)
                {
                    HuntApproachWatchdog.Register(hunter);
                }
            }
            catch (Exception ex)
            {
                __state = null;
                Log.ErrorOnce("[BetterHunters] cast-position prefix failed: " + ex, 0x5BE7B1);
            }
        }

        public static void Postfix(ref bool __result, CastPositionRequest newReq, ref IntVec3 dest, HuntEngagement __state)
        {
            if (__state == null || reentrant)
            {
                return;
            }

            try
            {
                if (!__result)
                {
                    // Narrowing the request left vanilla with nowhere to stand. Rather than let the hunt
                    // job fail outright, retry once with the untouched request so behaviour falls back to
                    // stock CE.
                    reentrant = true;
                    try
                    {
                        // Note: the real signature is (CastPositionRequest, out IntVec3). Harmony hands
                        // `out` parameters to patches as `ref`, hence the mismatch between this direct
                        // call and the patch method signatures above.
                        CastPositionRequest fallback = __state.originalRequest;
                        __result = CastPositionFinder.TryFindCastPosition(fallback, out IntVec3 fallbackDest);
                        dest = fallbackDest;
                    }
                    finally
                    {
                        reentrant = false;
                    }

                    if (!__result)
                    {
                        return; // truly nowhere to stand; nothing left to safeguard
                    }
                }
                else
                {
                    // Vanilla found a cell. If it landed inside the danger radius, pull it back out first;
                    // then, whatever cell we are left with, make sure it is not firing at a friendly.
                    MaybeRepickForRiskCap(__state, newReq, ref dest);
                }

                EnforceSafeBackdrop(__state, newReq, ref dest, ref __result);
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[BetterHunters] cast-position postfix failed: " + ex, 0x5BE7B2);
            }
        }

        /// <summary>
        /// Pulls the chosen cell back out to the safe annulus when the vanilla scorer picked one inside the
        /// risk cap (usually because it had good cover). No-op when the solver decided a close shot was fine
        /// (one-shot kill, harmless prey, or a weapon that cannot reach the safe distance anyway).
        /// </summary>
        private static void MaybeRepickForRiskCap(HuntEngagement st, CastPositionRequest newReq, ref IntVec3 dest)
        {
            BetterHuntersSettings s = BetterHuntersMod.Settings;

            float riskCap = st.solution.riskCap;
            if (riskCap <= 0f || st.solution.oneShotOverride || st.solution.engagementRange < riskCap)
            {
                return;
            }

            float chosenDist = (dest - st.victim.Position).LengthHorizontal;
            if (chosenDist >= riskCap)
            {
                return;
            }

            if (StandoffCells.TryFind(
                    st.hunter, st.victim, st.verb,
                    st.solution.riskCap, st.solution.engagementRange,
                    newReq.maxRangeFromCaster, newReq.locus, newReq.maxRangeFromLocus,
                    s, s.checkShotBackdrop, out IntVec3 better, out _))
            {
                dest = better;
            }
        }

        /// <summary>
        /// Final safety gate: never let the shot go off with a friendly pawn behind the prey. If the chosen
        /// cell has a hazardous backdrop, try to re-pick a cleaner angle from the safe annulus; if the only
        /// problem is a building it is left as-is (acceptable), but if a pawn is behind it and no clean angle
        /// exists the hunt is cancelled outright.
        /// </summary>
        private static void EnforceSafeBackdrop(HuntEngagement st, CastPositionRequest newReq, ref IntVec3 dest, ref bool __result)
        {
            BetterHuntersSettings s = BetterHuntersMod.Settings;
            if (!s.checkShotBackdrop)
            {
                return;
            }

            Map map = st.victim.Map;
            BackdropHazard destHazard = ShotBackdrop.Evaluate(map, dest, st.victim, st.hunter, s);
            if (destHazard == BackdropHazard.Clear)
            {
                return;
            }

            // Widen the ring a little when engagement sits right on the risk cap, matching StandoffCells.
            float maxDist = Mathf.Max(st.solution.engagementRange, st.solution.riskCap + 3f);

            bool found = StandoffCells.TryFind(
                st.hunter, st.victim, st.verb,
                st.solution.riskCap, maxDist,
                newReq.maxRangeFromCaster, newReq.locus, newReq.maxRangeFromLocus,
                s, true, out IntVec3 better, out BackdropHazard betterHazard);

            if (found && betterHazard < destHazard)
            {
                dest = better;
                return;
            }

            if (found)
            {
                return; // best available is no better than what we have (both fire over a building) - accept it
            }

            // No cell without a pawn behind the prey exists anywhere in range.
            if (destHazard == BackdropHazard.Pawn)
            {
                // Do not end the job here - we are inside the cast-position finder. Reporting failure lets
                // JobDriver_Hunt's GotoCastPosition toil end the job cleanly on its own.
                ShotBackdrop.AbortHuntForSafety(st.hunter, st.victim, s, endJob: false);
                __result = false;
                dest = IntVec3.Invalid;
            }

            // destHazard == Building with no cleaner option: firing over a building is acceptable, leave it.
        }
    }
}
