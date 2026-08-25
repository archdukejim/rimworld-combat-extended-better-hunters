using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BetterHunters
{
    /// <summary>
    /// Finds the best cell in the ring between a minimum and maximum distance from the prey: standable,
    /// reachable, unforbidden, inside any caller-supplied limits, and with a clear shot according to
    /// Combat Extended's own line-of-fire check (Verb.CanHitTargetFrom). "Best" is the cell nearest the
    /// hunter, i.e. the least walking.
    ///
    /// Shared by the cast-position postfix (which re-picks out of the danger radius the vanilla scorer
    /// wandered into) and the approach watchdog (which re-picks when the standoff went stale mid-hunt).
    /// </summary>
    internal static class StandoffCells
    {
        // Keep the scan bounded. Each examined cell costs a reachability check and a CE line-of-fire test,
        // so this caps the worst-case spike; the ring is walked nearest-the-prey first, so the useful
        // cells are hit early anyway.
        private const int MaxExamined = 250;

        /// <param name="maxRangeFromCaster">Caller cap on distance from the hunter, or 0 for none.</param>
        /// <param name="locus">Locus for <paramref name="maxRangeFromLocus"/>; ignored when that is 0.</param>
        /// <param name="maxRangeFromLocus">Caller cap on distance from the locus, or 0 for none.</param>
        internal static bool TryFind(
            Pawn hunter, Pawn victim, Verb verb,
            float minDist, float maxDist,
            float maxRangeFromCaster, IntVec3 locus, float maxRangeFromLocus,
            out IntVec3 result)
        {
            result = IntVec3.Invalid;

            Map map = victim.Map;
            if (map == null || hunter.Map != map)
            {
                return false;
            }

            // Give the ring some width even when the engagement range sits exactly on the risk cap
            // (which is what the "hold at the safety distance" branch of the solver produces).
            maxDist = Mathf.Max(maxDist, minDist + 3f);

            float bestScore = float.MaxValue;
            int examined = 0;

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
                if (maxRangeFromCaster > 0f
                    && (cell - hunter.Position).LengthHorizontal > maxRangeFromCaster)
                {
                    continue;
                }

                if (maxRangeFromLocus > 0f
                    && (cell - locus).LengthHorizontal > maxRangeFromLocus)
                {
                    continue;
                }

                if (cell.IsForbidden(hunter) || !hunter.CanReach(cell, PathEndMode.OnCell, Danger.Deadly))
                {
                    continue;
                }

                // CE's own check - covers both line of sight and CE's range/height rules.
                if (!verb.CanHitTargetFrom(cell, victim))
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
