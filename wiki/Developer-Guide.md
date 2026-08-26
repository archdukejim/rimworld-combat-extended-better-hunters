# Better Hunters (CE) — Developer's Guide

Architecture and integration notes for modders reading, extending, or debugging Better Hunters.
Source of truth is the code under `Source/BetterHunters/` on the tagged release; this guide
summarises it.

Better Hunters is a **behaviour mod**: no defs, no saved data. Everything is Harmony patches
plus reflection into Combat Extended (CE is never referenced at compile time). All CE access
degrades gracefully when CE's internals drift.

---

## What it patches

| Target | Kind | Purpose |
| --- | --- | --- |
| `Verse.AI.CastPositionFinder.TryFindCastPosition(CastPositionRequest, out IntVec3)` | prefix + postfix | Pick the engagement cell; the prefix also registers the hunt for re-checking |
| `CombatExtended.Verb_LaunchProjectileCE.TryStartCastOn(...)` | prefix | Deploy a CE bipod before the shot |
| `Verse.Game.FinalizeInit()` | postfix | Drop in-memory caches on load |

The approach re-check runs from a `GameComponent` (`BetterHuntersGameComponent`), **not** a
per-pawn Harmony patch — see [Approach watchdog](#approach-watchdog).

The mod exposes no formal public API for other mods to call; the types below are documented so
you can follow, patch around, or troubleshoot the behaviour.

---

## The hunt flow it hooks

Vanilla `RimWorld.JobDriver_Hunt.MakeNewToils` runs:

```
GotoCastPosition(A, MaxRangeFactor=0.95)          // walks to the cast cell (TryFindCastPosition here)
  -> slaughterIfPossible
     -> JumpIfTargetNotHittable(A, gotoCastPos)    // the ONLY vanilla reposition trigger
        -> CastVerb(A)                             // fires one cast, loops back
```

`TryFindCastPosition` runs **once** per approach. The fire loop only recomputes position when
line of fire is lost — never for safety. Both facts drive the design below.

---

## `HuntCastPositionPatch`

Patches the cast-position choke point. CE does not patch it (checked against CE's Harmony
directory), so there is no conflict.

- **Prefix** — for a colonist `JobDefOf.Hunt` job against a live `Pawn` with a CE ranged verb,
  solves the engagement (`EngagementSolver.TrySolve`) and narrows
  `CastPositionRequest.maxRangeFromTarget` down to the engagement range, so the vanilla scorer
  stops the hunter at easy-shot distance. It also calls `HuntApproachWatchdog.Register(hunter)`
  when the re-check setting is on — this is the single registration point for the watchdog, so
  no separate per-pawn patch is needed.
- **Postfix** — enforces the *minimum* distance (which `CastPositionRequest` has no field for)
  by re-picking a cell from the `[riskCap, engagementRange]` annulus via
  [`StandoffCells`](#standoffcells) when the vanilla choice landed inside the risk cap. If
  narrowing the request left vanilla with nowhere to stand, it retries once with the untouched
  request (guarded against re-entry) so the hunt degrades to stock CE instead of failing.

## `EngagementSolver`

Turns "which animal, which weapon" into "stand this far away". Produces an
`EngagementSolution { easyShotRange, riskCap, engagementRange, conflicted, oneShotOverride }`.

- **Easy-shot range** — from CE's own hit-chance math (`CeBindings.TryCalculateEasyShotRange`,
  backed by `CE_Math.CalculateMaxDistance`), clamped to the weapon's effective range × the
  hunt job's `MaxRangeFactor`. Accounts for a bipod's added range if one will be deployed.
- **Risk cap** — a minimum standoff from `RaceProperties.manhunterOnDamageChance` above the
  configured tolerance, scaled by body size / predator flag, plus a radial herd-mate scan.
- **Reconciliation** — if the risk cap is inside the easy-shot range there is no conflict; stand
  at the easy-shot edge. Otherwise hold at the safety distance, unless `closeForOneShotKill` and
  the round should drop the animal in one hit (`oneShotOverride`).

Results are cached per `(hunter, victim)` for ~240 ticks — the solve reflects into CE and does
a radial herd scan, so it is the heaviest recurring op; the cache keeps the watchdog's periodic
re-solves as cheap dictionary hits. Call `EngagementSolver.ClearCache()` on transitions.

## `StandoffCells`

`StandoffCells.TryFind(hunter, victim, verb, minDist, maxDist, maxRangeFromCaster, locus,
maxRangeFromLocus, out cell)` — the shared annulus search. Returns the nearest-to-hunter cell in
`[minDist, maxDist]` around the prey that is standable, reachable (`Danger.Deadly`), unforbidden,
inside any caller limits, and passes CE's own `Verb.CanHitTargetFrom`. Bounded to 250 examined
cells. Used by both the cast-position postfix and the watchdog.

## Approach watchdog

`HuntApproachWatchdog` is a registry of active hunters, ticked once per game tick from
`BetterHuntersGameComponent.GameComponentTick`. Using a component instead of a
`JobDriver.DriverTick` patch means **nothing runs for non-hunting pawns** — the only always-on
cost is the master-switch check plus one empty-set check per tick.

- **Registration** piggy-backs on `HuntCastPositionPatch.Prefix` (the one place that already
  identifies a managed hunt). Hunters are pruned automatically once they stop hunting.
- **`Tick`** snapshots the set (a break-off can synchronously re-issue a hunt and re-register),
  prunes stale pawns, and gates each on `IsHashIntervalTick(30)` before re-checking.
- **`Recheck`** re-solves against the prey's *current* position. If the committed cell is now
  inside `riskCap - 1`:
  - still walking in → re-route via `pather.StartPath` to a fresh `StandoffCells` cell;
  - already in position / cornered → `EndCurrentJob(InterruptForced)` to break off, and (if
    `pauseOnBreakOff`) pause the game + post a throttled `ThreatSmall` message.

  It bails on the solver's deliberate-close cases (`oneShotOverride`, `riskCap <= 0`,
  `engagementRange < riskCap`), on a burst in flight, and on the downed / corpse-collection
  phases.

Call `HuntApproachWatchdog.Reset()` on transitions (wired from `GameLifecycle`).

## `BipodDeployPatch`

Prefix on CE's `Verb_LaunchProjectileCE.TryStartCastOn`. If the equipped weapon has a
`CombatExtended.BipodComp`, raises the deploy intent so CE's own `JobDef_SetUpBipod` runs with
`resumeCurJobAfterwards: true`. A per-pawn attempt counter prevents a deploy loop. This is not
redundant with CE — CE's auto-setup and manual gizmo are both gated on `Drafted`, and hunters
never are.

## `CeBindings`

All CE access goes through here by reflection, with a documented fallback chain, so CE version
drift degrades gracefully instead of throwing. DefOf-backed lookups resolve lazily because Mod
constructors run before defs load. `CeBindings.CoreAvailable` gates every patch; if CE is
absent or an expected member is missing, the mod no-ops.

---

## Settings (`BetterHuntersSettings`)

`enabled`, `easyShotHitChance`, `maxRevengeChance`, `revengeDistanceScale`, `herdSensitivity`,
`herdScanRadius`, `maxRiskCapCells`, `closeForOneShotKill`, `deployBipod`,
`recheckDuringApproach`, `pauseOnBreakOff`, `debugLogging`. All are `Scribe_Values` with
defaults; new keys are additive, so settings files stay forward/backward compatible.

## Debugging

Enable **Debug logging** in mod settings to log, per hunt: the easy-shot range, risk cap, final
engagement range, whether the solver used CE's native solver or the fallback, mid-approach
re-routes, and break-offs. `[BetterHunters]` prefixes every line.
