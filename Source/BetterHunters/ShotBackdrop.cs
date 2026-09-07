using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BetterHunters
{
    /// <summary>How bad the backdrop behind the prey is, worst-first when compared numerically.</summary>
    public enum BackdropHazard
    {
        /// <summary>Nothing worth protecting downrange (open ground, natural rock, hostiles).</summary>
        Clear = 0,

        /// <summary>A player-owned building sits behind the prey - not ideal, but a shot may still be taken.</summary>
        Building = 1,

        /// <summary>A friendly pawn is behind the prey. A shot from here is never acceptable.</summary>
        Pawn = 2,
    }

    /// <summary>
    /// "Know your target and what's beyond it." Combat Extended projectiles are ballistic: a missed shot
    /// deviates by an angle and keeps flying <em>past</em> the target until something stops it. So the
    /// danger of a stray is whatever sits in the line of fire beyond the prey, up to the first wall.
    ///
    /// This walks that corridor - starting one cell past the prey, widening slightly with distance to
    /// approximate the spreading miss cone, and stopping at the first full-height edifice (which safely
    /// absorbs the shot) - and reports the worst thing found:
    ///   * a friendly pawn  -> <see cref="BackdropHazard.Pawn"/> (a hard no; the reason this feature exists)
    ///   * a player building -> <see cref="BackdropHazard.Building"/> (soft; avoided only when an alternative exists)
    /// Natural rock walls carry no faction, so they are never a hazard - they are the ideal backstop.
    /// </summary>
    internal static class ShotBackdrop
    {
        /// <summary>Widen the checked corridor by one cell per this many cells of depth, so a shot that
        /// strays a little further out the longer it flies is still caught.</summary>
        private const float SpreadPerCell = 0.15f;

        /// <summary>Cap on the corridor half-width; past this the miss cone is too wide to model usefully.</summary>
        private const int MaxHalfWidth = 3;

        /// <summary>
        /// Worst hazard in the line of fire from <paramref name="firingCell"/> through
        /// <paramref name="victim"/> and onward. Returns <see cref="BackdropHazard.Clear"/> when the
        /// feature is off, so callers can call it unconditionally.
        /// </summary>
        internal static BackdropHazard Evaluate(
            Map map, IntVec3 firingCell, Pawn victim, Pawn hunter, BetterHuntersSettings s)
        {
            if (map == null || victim == null || s == null || !s.checkShotBackdrop)
            {
                return BackdropHazard.Clear;
            }

            IntVec3 vpos = victim.Position;

            Vector3 origin = firingCell.ToVector3Shifted();
            Vector3 preyCenter = vpos.ToVector3Shifted();
            Vector3 dir = preyCenter - origin;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
            {
                // Firing from on top of the prey - no meaningful line to trace.
                return BackdropHazard.Clear;
            }

            dir = dir.normalized;
            Vector3 perp = new Vector3(-dir.z, 0f, dir.x); // horizontal, perpendicular to the shot line

            Faction player = Faction.OfPlayer;
            int length = Mathf.Max(1, Mathf.RoundToInt(s.backdropCheckRange));
            BackdropHazard worst = BackdropHazard.Clear;

            for (int step = 1; step <= length; step++)
            {
                Vector3 centerV = preyCenter + dir * step;
                IntVec3 center = centerV.ToIntVec3();
                if (!center.InBounds(map))
                {
                    break; // ran off the map edge; nothing of the colony's is out there
                }

                int halfWidth = Mathf.Min(MaxHalfWidth, 1 + (int)(step * SpreadPerCell));
                for (int off = -halfWidth; off <= halfWidth; off++)
                {
                    IntVec3 cell = (centerV + perp * off).ToIntVec3();
                    if (!cell.InBounds(map) || cell == vpos)
                    {
                        continue;
                    }

                    BackdropHazard here = HazardAt(map, cell, victim, hunter, player, s);
                    if (here == BackdropHazard.Pawn)
                    {
                        return BackdropHazard.Pawn; // cannot get worse; stop scanning
                    }

                    if (here > worst)
                    {
                        worst = here;
                    }
                }

                // A full-height edifice (a wall, natural or built) stops the projectile: nothing past it
                // can be hit, so the scan ends here.
                Building edifice = center.GetEdifice(map);
                if (edifice != null && edifice.def.Fillage == FillCategory.Full)
                {
                    break;
                }
            }

            return worst;
        }

        private static BackdropHazard HazardAt(
            Map map, IntVec3 cell, Pawn victim, Pawn hunter, Faction player, BetterHuntersSettings s)
        {
            BackdropHazard worst = BackdropHazard.Clear;

            List<Thing> things = cell.GetThingList(map);
            for (int i = 0; i < things.Count; i++)
            {
                Thing t = things[i];

                if (t is Pawn p)
                {
                    if (p == victim || p == hunter)
                    {
                        continue;
                    }

                    // Anything factioned that is not hostile to us is something we must not shoot toward:
                    // colonists, tamed animals, prisoners, visitors, allies. Wild animals (no faction) and
                    // hostiles are not protected here - hitting a wild animal is a separate concern and
                    // hitting an enemy is fine.
                    if (p.Faction != null && !p.Faction.HostileTo(player))
                    {
                        return BackdropHazard.Pawn;
                    }
                }
                else if (s.avoidBuildingBackdrop && t is Building b)
                {
                    if (b.Faction == player && b.def.useHitPoints && worst < BackdropHazard.Building)
                    {
                        worst = BackdropHazard.Building;
                    }
                }
            }

            return worst;
        }

        /// <summary>
        /// No firing position exists without a friendly pawn behind the prey. Cancel the hunt outright -
        /// remove the Hunt designation so no colonist re-attempts the unsafe shot - and tell the player why.
        /// </summary>
        /// <param name="endJob">
        /// True from the mid-approach watchdog, which must stop the running job itself. False from the
        /// cast-position patch, where returning a failed result lets vanilla end the job cleanly (ending it
        /// from inside the finder would re-enter the job system mid-toil).
        /// </param>
        internal static void AbortHuntForSafety(Pawn hunter, Pawn victim, BetterHuntersSettings s, bool endJob)
        {
            Map map = victim?.Map;
            bool removed = false;

            if (map?.designationManager != null)
            {
                Designation des = map.designationManager.DesignationOn(victim, DesignationDefOf.Hunt);
                if (des != null)
                {
                    map.designationManager.RemoveDesignation(des);
                    removed = true;
                }
            }

            // Only announce when we were the one that actually cancelled it, so two hunters aborting on the
            // same prey in the same tick don't double up the message.
            if (removed)
            {
                Messages.Message(
                    $"Better Hunters cancelled the hunt of {victim.LabelShort}: no firing spot without a "
                    + "colonist in the line of fire. Re-mark it once the area is clear.",
                    new LookTargets(victim), MessageTypeDefOf.CautionInput, historical: false);

                if (s != null && s.debugLogging)
                {
                    Log.Message($"[BetterHunters] {hunter?.LabelShort} aborted hunt of {victim.LabelShort} "
                                + "for backdrop safety; Hunt designation removed.");
                }
            }

            if (endJob && hunter?.jobs != null && hunter.CurJobDef == JobDefOf.Hunt)
            {
                hunter.jobs.EndCurrentJob(JobCondition.InterruptForced);
            }
        }
    }
}
