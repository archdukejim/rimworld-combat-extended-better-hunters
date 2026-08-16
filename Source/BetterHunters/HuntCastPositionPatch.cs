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

                    return;
                }

                float riskCap = __state.solution.riskCap;
                if (riskCap <= 0f || __state.solution.oneShotOverride)
                {
                    return;
                }

                // If the weapon cannot even reach the safe distance, the engagement range was clamped
                // below the risk cap and there is no safe cell that can also take the shot. Leave
                // vanilla's choice alone rather than pushing the hunter somewhere it cannot fire from.
                if (__state.solution.engagementRange < riskCap)
                {
                    return;
                }

                float chosenDist = (dest - __state.victim.Position).LengthHorizontal;
                if (chosenDist >= riskCap)
                {
                    return;
                }

                // Vanilla picked a cell inside the danger radius (usually because it had good cover).
                // Re-pick from the annulus [riskCap, engagementRange].
                if (TryFindStandoffCell(__state, newReq, out IntVec3 better))
                {
                    dest = better;
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[BetterHunters] cast-position postfix failed: " + ex, 0x5BE7B2);
            }
        }

        /// <summary>
        /// Picks the best cell in the ring between the risk cap and the engagement range: must be
        /// standable, reachable, inside the request's own limits, and have a clear shot at the prey
        /// according to CE's own line-of-fire check (Verb_LaunchProjectileCE.CanHitTargetFrom).
        /// Among valid cells we take the one nearest the hunter, i.e. the least walking.
        /// </summary>
        private static bool TryFindStandoffCell(HuntEngagement state, CastPositionRequest req, out IntVec3 result)
        {
            result = IntVec3.Invalid;

            Pawn hunter = state.hunter;
            Pawn victim = state.victim;
            Map map = victim.Map;

            if (map == null || hunter.Map != map)
            {
                return false;
            }

            float minDist = state.solution.riskCap;
            // Give the ring some width even when the engagement range sits exactly on the risk cap
            // (which is what the "hold at the safety distance" branch of the solver produces).
            float maxDist = Mathf.Max(state.solution.engagementRange, minDist + 3f);

            float bestScore = float.MaxValue;
            int examined = 0;
            const int MaxExamined = 400; // keep the scan bounded; hunts are frequent

            foreach (IntVec3 cell in GenRadial.RadialCellsAround(victim.Position, minDist, maxDist))
            {
                if (++examined > MaxExamined)
                {
                    break;
                }

                if (!cell.InBounds(map) || !cell.Standable(map))
                {
                    continue;
                }

                // Respect the limits the caller put on the request.
                if (req.maxRangeFromCaster > 0f
                    && (cell - hunter.Position).LengthHorizontal > req.maxRangeFromCaster)
                {
                    continue;
                }

                if (req.maxRangeFromLocus > 0f
                    && (cell - req.locus).LengthHorizontal > req.maxRangeFromLocus)
                {
                    continue;
                }

                if (cell.IsForbidden(hunter) || !hunter.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }

                // CE's own check - covers both line of sight and CE's range/height rules.
                if (!state.verb.CanHitTargetFrom(cell, victim))
                {
                    continue;
                }

                float score = (cell - hunter.Position).LengthHorizontalSquared;
                if (score < bestScore)
                {
                    bestScore = score;
                    result = cell;
                }
            }

            return result.IsValid;
        }
    }
}
