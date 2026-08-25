using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BetterHunters
{
    /// <summary>
    /// Keeps the engagement decision honest for the whole approach, not just the instant it is made.
    ///
    /// The cast-position patch (<see cref="HuntCastPositionPatch"/>) picks where the hunter stands, but it
    /// runs once - when <c>JobDriver_Hunt</c> sets up its <c>GotoCastPosition</c> toil. After that vanilla
    /// commits: the pawn walks the whole path, and its fire loop is
    ///     slaughterIfPossible -> JumpIfTargetNotHittable(gotoCastPos) -> CastVerb -> jump back
    /// The only thing that sends it back to recompute is losing line of fire. Nothing re-checks whether
    /// the chosen standoff is still <em>safe</em>. So if the prey advances while the hunter is walking in
    /// or lining up the shot, the safe distance goes stale and it fires from inside the risk cap anyway -
    /// the "it never double-checked" behaviour seen in play.
    ///
    /// This is a small registry of active hunters, ticked from <see cref="BetterHuntersGameComponent"/>.
    /// Registration is piggy-backed on the cast-position patch (the one place that already identifies a
    /// managed hunt), so there is <em>no per-tick Harmony patch on every pawn's job driver</em> - the only
    /// always-on cost is one empty-set check per game tick. Each registered hunter is re-checked at a
    /// throttled interval against the prey's <em>current</em> position, and when the standoff has gone
    /// stale one of two things happens:
    ///   - still walking in : re-route to a fresh safe cell (no shot in progress, so seamless).
    ///   - already in position (or nowhere safe to walk to) : break off the hunt so the colonist
    ///     disengages. A prey that has closed the distance <em>onto</em> the hunter is the rare
    ///     "&lt;predator&gt; is hunting &lt;colonist&gt; for food" situation, better handled by stopping.
    /// </summary>
    public static class HuntApproachWatchdog
    {
        /// <summary>Slack (cells) so a hunter sitting right at the risk cap does not thrash in and out.</summary>
        private const float Hysteresis = 1f;

        /// <summary>How often each hunter's standoff is re-checked. Half a second is responsive enough.</summary>
        private const int RecheckIntervalTicks = 30;

        /// <summary>
        /// Minimum ticks between pause/notify for the same hunter, so a break -> re-issue -> break chase
        /// cannot spam letters or wrestle the game speed away from the player. Matches vanilla's own
        /// predator-hunting-colonist notification cadence (~42s).
        /// </summary>
        private const int NotifyThrottleTicks = 2500;

        /// <summary>Currently managed hunters. Populated by the cast-position patch, pruned as they stop.</summary>
        private static readonly HashSet<Pawn> Watched = new HashSet<Pawn>();

        /// <summary>Reused each tick so a re-entrant registration can't mutate the set mid-iteration.</summary>
        private static readonly List<Pawn> TickBuffer = new List<Pawn>();
        private static readonly List<Pawn> ToRemove = new List<Pawn>();

        /// <summary>Per-hunter tick of the last break-off pause/notify. Cleared on load via <see cref="Reset"/>.</summary>
        private static readonly Dictionary<int, int> LastBreakNoticeTick = new Dictionary<int, int>();

        /// <summary>Adds a hunter to the watch set. Idempotent; called when a managed hunt sets up.</summary>
        public static void Register(Pawn hunter)
        {
            if (hunter != null)
            {
                Watched.Add(hunter);
            }
        }

        /// <summary>Drops all watch and throttle state. Called on map/game transitions.</summary>
        public static void Reset()
        {
            Watched.Clear();
            TickBuffer.Clear();
            ToRemove.Clear();
            LastBreakNoticeTick.Clear();
        }

        /// <summary>
        /// Ticked once per game tick by the game component. Iterates only the handful of active hunters -
        /// nothing runs for the rest of the colony - and re-checks each on its throttled interval.
        /// </summary>
        public static void Tick(BetterHuntersSettings s)
        {
            if (Watched.Count == 0)
            {
                return;
            }

            // Snapshot first: Recheck can end a job, which may synchronously start another and re-register
            // a hunter, and mutating Watched while iterating it would throw.
            TickBuffer.Clear();
            TickBuffer.AddRange(Watched);

            for (int i = 0; i < TickBuffer.Count; i++)
            {
                Pawn hunter = TickBuffer[i];

                if (hunter == null || !hunter.Spawned || hunter.CurJobDef != JobDefOf.Hunt)
                {
                    ToRemove.Add(hunter);
                    continue;
                }

                if (!hunter.IsHashIntervalTick(RecheckIntervalTicks))
                {
                    continue;
                }

                try
                {
                    Recheck(hunter, s);
                }
                catch (Exception ex)
                {
                    Log.ErrorOnce("[BetterHunters] approach watchdog failed: " + ex, 0x5BE7B3);
                }
            }

            if (ToRemove.Count > 0)
            {
                for (int i = 0; i < ToRemove.Count; i++)
                {
                    Watched.Remove(ToRemove[i]);
                }

                ToRemove.Clear();
            }
        }

        private static void Recheck(Pawn hunter, BetterHuntersSettings s)
        {
            Job job = hunter.CurJob;
            if (job == null || job.def != JobDefOf.Hunt)
            {
                return;
            }

            // verbToUse is what CastVerb actually fires; Toils_Combat.TrySetJobToUseAttackVerb sets it one
            // toil before the walk starts, so it is null only in the opening instant of the job.
            Verb verb = job.verbToUse;
            if (verb == null || !CeBindings.IsCeRangedVerb(verb))
            {
                return;
            }

            // Never interrupt a burst already in flight; re-check once it finishes.
            if (verb.state == VerbState.Bursting)
            {
                return;
            }

            // Target A is the corpse once the prey is dead (collection phase) - not a live Pawn - so that
            // phase drops out here. A downed prey means the melee-execute phase, where standing off is
            // wrong, so leave that alone too.
            if (!(job.GetTarget(TargetIndex.A).Thing is Pawn victim)
                || victim.Dead || victim.Downed || !victim.Spawned || victim.Map != hunter.Map)
            {
                return;
            }

            if (!EngagementSolver.TrySolve(hunter, victim, verb, out EngagementSolution sol))
            {
                return;
            }

            // Cases where the solver deliberately did not push the hunter out to a safe standoff:
            //   oneShotOverride  - it chose to close for a clean kill, which provokes nothing.
            //   riskCap <= 0     - the prey retaliates for nothing.
            //   engage < riskCap - the weapon cannot even reach the safe distance, so no cell is both safe
            //                      and able to fire (mirrors HuntCastPositionPatch.Postfix).
            if (sol.oneShotOverride || sol.riskCap <= 0f || sol.engagementRange < sol.riskCap)
            {
                return;
            }

            float safeDist = sol.riskCap - Hysteresis;
            bool moving = hunter.pather.Moving;
            IntVec3 committed = moving ? hunter.pather.Destination.Cell : hunter.Position;

            if ((committed - victim.Position).LengthHorizontal >= safeDist)
            {
                return; // still safe, and any walk in progress is toward a safe cell
            }

            // The standoff went stale. If still walking in, quietly re-route to a fresh safe cell - no shot
            // is in progress to disturb.
            float maxDist = Mathf.Max(sol.engagementRange, sol.riskCap + 3f);
            if (moving
                && StandoffCells.TryFind(hunter, victim, verb, sol.riskCap, maxDist, 0f, IntVec3.Invalid, 0f,
                    out IntVec3 safeCell)
                && hunter.pather.Destination.Cell != safeCell)
            {
                hunter.pather.StartPath(safeCell, PathEndMode.OnCell);
                hunter.Map.pawnDestinationReservationManager.Reserve(hunter, job, safeCell);

                if (s.debugLogging)
                {
                    Log.Message($"[BetterHunters] {hunter.LabelShort} re-routed mid-approach to {safeCell} "
                                + $"({(safeCell - victim.Position).LengthHorizontal:F1} cells from prey); "
                                + $"{victim.LabelShort} had closed toward the intended spot.");
                }

                return;
            }

            // Already in position, or cornered with nowhere safe to walk to: the prey has closed the
            // distance onto the hunter. Break off - the colonist disengages rather than shooting point
            // blank at something now within its safety distance.
            if (s.debugLogging)
            {
                Log.Message($"[BetterHunters] {hunter.LabelShort} breaking off hunt of {victim.LabelShort}: "
                            + $"prey is inside the {sol.riskCap:F1}-cell safety distance and cannot be "
                            + "re-approached safely from here.");
            }

            if (s.pauseOnBreakOff)
            {
                NotifyBreakOff(hunter, victim);
            }

            hunter.jobs.EndCurrentJob(JobCondition.InterruptForced);
        }

        /// <summary>
        /// Pauses the game and drops a message on the hunter so the player can take over, throttled per
        /// hunter. Only fires for genuinely dangerous prey - a break-off only happens when the risk cap is
        /// above zero, i.e. the animal can actually turn on the hunter.
        /// </summary>
        private static void NotifyBreakOff(Pawn hunter, Pawn victim)
        {
            int now = Find.TickManager.TicksGame;
            if (LastBreakNoticeTick.TryGetValue(hunter.thingIDNumber, out int last)
                && now - last < NotifyThrottleTicks)
            {
                return;
            }

            if (LastBreakNoticeTick.Count > 256)
            {
                LastBreakNoticeTick.Clear();
            }

            LastBreakNoticeTick[hunter.thingIDNumber] = now;

            // Plain English to match the rest of the mod (no Languages folder / .Translate usage).
            Messages.Message(
                $"{hunter.LabelShort} broke off hunting {victim.LabelShort}: it closed inside the safe distance.",
                hunter, MessageTypeDefOf.ThreatSmall, historical: false);

            Find.TickManager.Pause();
        }
    }
}
