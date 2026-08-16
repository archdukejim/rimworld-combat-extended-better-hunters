using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace BetterHunters
{
    /// <summary>Result of working out where a hunter should stand.</summary>
    public struct EngagementSolution
    {
        /// <summary>Outer edge of the band where CE says the shot meets the easy-shot threshold.</summary>
        public float easyShotRange;

        /// <summary>Minimum distance the hunter should keep, from revenge + herd risk.</summary>
        public float riskCap;

        /// <summary>Where the hunter should actually stand.</summary>
        public float engagementRange;

        /// <summary>True when no easy shot exists outside the risk cap.</summary>
        public bool conflicted;

        /// <summary>True when we accepted the risky approach because the shot should be lethal in one hit.</summary>
        public bool oneShotOverride;
    }

    /// <summary>
    /// Turns "which animal, which weapon" into "stand this far away".
    ///
    /// Two independent numbers are computed and then reconciled:
    ///   1. Easy-shot range - from Combat Extended's own hit-chance math, so "easy" means what CE
    ///      will actually roll. See CeBindings.TryCalculateEasyShotRange.
    ///   2. Risk cap - a minimum standoff from RaceProperties.manhunterOnDamageChance, the animal's
    ///      body size / predator flag, and a radial scan for herd-mates.
    /// </summary>
    public static class EngagementSolver
    {
        // Vanilla's own floor for a cast position (Toils_Combat.GotoCastPosition); never ask for closer.
        private const float MinSensibleRange = 1.42f;

        private struct CacheEntry
        {
            public int tick;
            public EngagementSolution solution;
            public bool valid;
        }

        private static readonly Dictionary<int, CacheEntry> Cache = new Dictionary<int, CacheEntry>();
        private const int CacheLifetimeTicks = 120;

        /// <summary>
        /// Works out the engagement range for this hunter/prey/weapon triple.
        /// Cached briefly because CastPositionFinder can be re-entered several times per approach.
        /// </summary>
        public static bool TrySolve(Pawn hunter, Pawn victim, Verb verb, out EngagementSolution solution)
        {
            solution = default(EngagementSolution);

            if (hunter == null || victim == null || verb == null || victim.Map == null)
            {
                return false;
            }

            if (!CeBindings.CoreAvailable || !CeBindings.IsCeRangedVerb(verb))
            {
                return false;
            }

            int key = Gen.HashCombineInt(hunter.thingIDNumber, victim.thingIDNumber);
            int now = Find.TickManager.TicksGame;

            if (Cache.TryGetValue(key, out CacheEntry cached) && now - cached.tick < CacheLifetimeTicks)
            {
                solution = cached.solution;
                return cached.valid;
            }

            bool ok = SolveUncached(hunter, victim, verb, out solution);

            if (Cache.Count > 256)
            {
                Cache.Clear();
            }

            Cache[key] = new CacheEntry { tick = now, solution = solution, valid = ok };
            return ok;
        }

        private static bool SolveUncached(Pawn hunter, Pawn victim, Verb verb, out EngagementSolution solution)
        {
            solution = default(EngagementSolution);

            // CE_Utility.GetBoundsFor gives the same target silhouette CE's hit math uses.
            if (!CeBindings.TryGetBounds(victim, out Bounds bounds))
            {
                return false;
            }

            if (!CeBindings.TryGetAimParams(verb, out CeAimParams aim))
            {
                return false;
            }

            BetterHuntersSettings s = BetterHuntersMod.Settings;

            if (!CeBindings.TryCalculateEasyShotRange(s.easyShotHitChance, bounds, aim, out float easyRange))
            {
                return false;
            }

            // Never propose standing outside what the weapon can actually reach. JobDriver_Hunt applies
            // its own MaxRangeFactor to the verb range; mirror that so we stay inside vanilla's envelope.
            // Verb.EffectiveRange is virtual and CE overrides it (Verb_LaunchProjectileCE.cs:148) to fold
            // in RangeMultiplier stats and projectile range offsets, so it is the honest ceiling.
            float weaponRange = verb.EffectiveRange;
            if (s.deployBipod)
            {
                // If a bipod is fitted it will be deployed before the shot, and CE's BipodComp.SetUpEnd
                // adds `additionalrange` to the verb at that point.
                ThingComp bipod = CeBindings.GetBipodComp(hunter.equipment?.Primary);
                if (bipod != null && !CeBindings.IsBipodDeployed(bipod))
                {
                    weaponRange += CeBindings.GetBipodAdditionalRange(bipod);
                }
            }

            float maxUsable = Mathf.Max(MinSensibleRange, weaponRange * JobDriver_Hunt.MaxRangeFactor);
            easyRange = Mathf.Clamp(easyRange, MinSensibleRange, maxUsable);

            float riskCap = Mathf.Min(ComputeRiskCap(victim, s), s.maxRiskCapCells);

            solution.easyShotRange = easyRange;
            solution.riskCap = riskCap;

            if (riskCap <= easyRange)
            {
                // No conflict: stand at the far edge of the easy-shot band. That is the least approach
                // that still yields the wanted hit chance, which is also the safest of the valid options.
                solution.engagementRange = easyRange;
            }
            else
            {
                // The easy shot is only available inside the risky zone.
                solution.conflicted = true;

                if (s.closeForOneShotKill && LikelyDropsInOneHit(verb, victim, aim))
                {
                    // A clean kill provokes nothing, so take the good shot.
                    solution.engagementRange = easyRange;
                    solution.oneShotOverride = true;
                }
                else
                {
                    // Hold at the safety distance and accept the worse shot rather than start a fight.
                    solution.engagementRange = Mathf.Min(riskCap, maxUsable);
                }
            }

            if (s.debugLogging)
            {
                Log.Message($"[BetterHunters] {hunter.LabelShort} -> {victim.LabelShort}: "
                            + $"easy={solution.easyShotRange:F1} riskCap={solution.riskCap:F1} "
                            + $"engage={solution.engagementRange:F1} "
                            + $"conflict={solution.conflicted} oneShot={solution.oneShotOverride} "
                            + $"(solver={(CeBindings.HasNativeSolver ? "CE_Math" : "fallback")})");
            }

            return true;
        }

        /// <summary>
        /// Minimum standoff, in cells, driven by how likely and how bad retaliation would be.
        ///
        /// Revenge term  : RaceProperties.manhunterOnDamageChance above the player's tolerance, scaled by danger.
        /// Herd term     : conspecifics within the scan radius, scaled by danger. Only counted when the
        ///                 species can actually turn on the hunter at all.
        /// Danger factor : RaceProperties.baseBodySize, doubled for RaceProperties.predator.
        /// </summary>
        private static float ComputeRiskCap(Pawn victim, BetterHuntersSettings s)
        {
            RaceProperties race = victim.RaceProps;
            if (race == null)
            {
                return 0f;
            }

            float manhunterChance = race.manhunterOnDamageChance;
            float danger = Mathf.Clamp(race.baseBodySize, 0.2f, 4f) * (race.predator ? 2f : 1f);

            float excess = Mathf.Max(0f, manhunterChance - s.maxRevengeChance);
            float revengeDist = excess * s.revengeDistanceScale * danger;

            float herdDist = 0f;
            if (manhunterChance > 0.01f && s.herdSensitivity > 0f && s.herdScanRadius > 0f)
            {
                int herd = CountHerdMates(victim, s.herdScanRadius);
                herdDist = herd * s.herdSensitivity * danger;
            }

            return Mathf.Max(0f, revengeDist + herdDist);
        }

        /// <summary>
        /// Radial scan for animals of the same species that could join a manhunter response.
        /// Wild animals only - tamed and faction animals do not pile on.
        /// </summary>
        private static int CountHerdMates(Pawn victim, float radius)
        {
            const int Cap = 16; // stop counting past the point where more animals change nothing

            int count = 0;
            foreach (Thing thing in GenRadial.RadialDistinctThingsAround(victim.Position, victim.Map, radius, false))
            {
                if (!(thing is Pawn other))
                {
                    continue;
                }

                if (other == victim || other.Dead || other.Faction != null)
                {
                    continue;
                }

                if (other.def == victim.def)
                {
                    count++;
                    if (count >= Cap)
                    {
                        break;
                    }
                }
            }

            return count;
        }

        /// <summary>
        /// Rough "will this drop it outright" test: compare the projectile's damage against the health
        /// of the animal's core body part. Deliberately conservative - it is only used to justify
        /// ignoring the safety standoff.
        ///
        /// FLAG: an approximation. CE resolves damage through its own armour and ammo pipeline
        /// (CombatExtended.ArmorUtilityCE), which this does not model.
        /// </summary>
        private static bool LikelyDropsInOneHit(Verb verb, Pawn victim, CeAimParams aim)
        {
            ThingDef projectile = aim.projectile;
            if (projectile?.projectile == null || victim.health?.hediffSet == null)
            {
                return false;
            }

            BodyPartRecord core = victim.RaceProps?.body?.corePart;
            if (core == null)
            {
                return false;
            }

            float corePartHealth;
            try
            {
                corePartHealth = victim.health.hediffSet.GetPartHealth(core);
            }
            catch
            {
                return false;
            }

            if (corePartHealth <= 0f)
            {
                return true;
            }

            int damage;
            try
            {
                damage = projectile.projectile.GetDamageAmount(verb.EquipmentSource, null);
            }
            catch
            {
                return false;
            }

            return damage >= corePartHealth;
        }

        /// <summary>Drops cached solutions. Called on map/game transitions.</summary>
        public static void ClearCache() => Cache.Clear();
    }
}
