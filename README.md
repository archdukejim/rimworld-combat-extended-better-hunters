# Better Hunters (Combat Extended)

A RimWorld mod that makes Combat Extended hunters **position before they shoot**.

Under CE a hunter opens fire the moment the animal enters weapon range. Weapon ranges are long and
CE's hit chance falls off hard with distance, so the usual result is a string of low-probability
shots that wound the animal, which then flees or turns manhunter. This mod makes the hunter walk to
a distance where CE's own hit-chance math says the shot is easy, then fire — while refusing to close
further than a safety standoff scaled to how dangerous retaliation would be.

**Hard dependency on [Combat Extended](https://github.com/CombatExtended-Continued/CombatExtended)**
(`CETeam.CombatExtended`). Behaviour-only: no new defs, no saved data, safe to add or remove mid-save.

---

## How it decides where to stand

Two numbers are computed independently and then reconciled.

### 1. Easy-shot range

The outer edge of the band where CE says the first shot meets the hit-chance threshold. Because hit
probability decreases monotonically with distance, anywhere at or inside that edge is an "easy" shot,
so standing *on* the edge is the least approach that still buys the wanted accuracy.

This comes from CE's own solver, `CombatExtended.CE_Math.CalculateMaxDistance`, fed the same aiming
inputs CE puts into its `ShiftVecReport`: `AimingAccuracy`, `SightsEfficiency`, `SwayAmplitude`,
`ShotSpread × spreadMult`, projectile speed, and `CE_Utility.GetBoundsFor(animal)` for the target
silhouette. Using CE's function rather than a reimplementation means "easy" means what CE will
actually roll.

### 2. Risk cap (minimum distance)

```
danger      = clamp(RaceProperties.baseBodySize, 0.2, 4) × (RaceProperties.predator ? 2 : 1)
revengeDist = max(0, manhunterOnDamageChance − toleratedRevengeChance) × revengeScale × danger
herdDist    = (conspecifics within scan radius) × herdSensitivity × danger
riskCap     = min(revengeDist + herdDist, maxStandoff)
```

The herd term is only counted for species that can actually turn on the hunter
(`manhunterOnDamageChance > 0.01`), and only wild animals are counted — tamed and faction animals
don't pile on.

### 3. Reconciliation

| Case | Result |
| --- | --- |
| `riskCap ≤ easyRange` | Stand at `easyRange`. Least approach that is still an easy shot. |
| Conflict, shot should drop it in one hit | Close to `easyRange` anyway — a clean kill provokes nothing. |
| Conflict otherwise | Hold at `riskCap`, accept the worse shot rather than start a fight. |

The one-hit test compares the projectile's damage against the health of the animal's core body part.
It is an approximation — CE resolves damage through its own armour/ammo pipeline, which this does not
model — and it is only ever used to justify ignoring the standoff, never to extend it.

---

## What gets patched

| Target | Kind | Purpose |
| --- | --- | --- |
| `Verse.AI.CastPositionFinder.TryFindCastPosition(CastPositionRequest, out IntVec3)` | prefix + postfix | The engagement-range decision (prefix also registers the hunt for re-checking) |
| `CombatExtended.Verb_LaunchProjectileCE.TryStartCastOn(...)` | prefix | Deploy bipod before the shot |
| `Verse.Game.FinalizeInit()` | postfix | Drop in-memory caches on load |

The approach re-check runs from a `GameComponent` (`BetterHuntersGameComponent`), **not** a per-pawn
Harmony patch — see below.

The hunt approach chain is:

```
RimWorld.JobDriver_Hunt.MakeNewToils
  └─ Verse.AI.Toils_Combat.GotoCastPosition(..., JobDriver_Hunt.MaxRangeFactor)   // = 0.95
       └─ Verse.AI.CastPositionFinder.TryFindCastPosition(newReq, out dest)       // ← patched here
```

`CastPositionFinder` is the right choke point: it is the single place "where does the hunter stand"
is decided, it is a plain static method rather than a compiler-generated iterator, and **CE does not
patch it** (verified against CE's full Harmony patch directory), so there is no conflict.

- **Prefix** narrows `CastPositionRequest.maxRangeFromTarget` to the engagement range. Vanilla's
  scorer prefers cells near the caster, so with that ceiling the hunter walks in only until the shot
  becomes easy, then stops.
- **Postfix** enforces the *minimum* distance, which `CastPositionRequest` has no field for, by
  re-picking from the annulus `[riskCap, engagementRange]` when vanilla chose a closer cell (usually
  because it had good cover). Candidates must be standable, reachable, within the request's own
  limits, and pass CE's own line-of-fire check `Verb_LaunchProjectileCE.CanHitTargetFrom`.
- If narrowing the request leaves vanilla with nowhere to stand, the postfix retries **once** with the
  untouched request (guarded against re-entry) so the hunt degrades to stock CE instead of failing.

`TryFindCastPosition` runs **once**, when the `GotoCastPosition` toil is set up. After that vanilla
commits to the cell: the pawn walks the whole path, and its fire loop only recomputes position when it
*loses line of fire* — never when the standoff simply becomes unsafe. So a prey that advances, or a herd
that gathers, while the hunter is walking in or lining up the shot leaves the chosen distance stale.

The **approach watchdog** closes that gap by re-checking the standoff against the prey's *current*
position and reacting in one of two ways:

- **Still walking in** — if the cell the hunter is heading to is now inside the risk cap, it re-routes to
  a fresh safe cell (same `[riskCap, engagementRange]` annulus search the cast-position postfix uses).
  No shot is in progress, so this is seamless.
- **Already in position** (or cornered with nowhere safe to walk to) — the prey has closed the distance
  *onto* the hunter. The hunter **breaks off the hunt** (`EndCurrentJob`) and disengages rather than
  shooting point-blank. In practice this is the rare "*&lt;predator&gt; is hunting &lt;colonist&gt; for
  food*" situation, better handled by stopping than by kiting.

On break-off, with **Pause and flag the pawn on break-off** on (default), the mod pauses the game and
jumps a `ThreatSmall` message to the hunter so the player can take over. It is throttled to once per
~42s per hunter (matching vanilla's own predator-hunting-colonist cadence) so a chase can't spam letters
or wrestle game speed from the player. This is well-targeted because a break-off only happens when the
risk cap is above zero — a docile animal that merely wanders close never triggers one. It complements the
game's built-in pause, which only fires once an animal escalates to a full `ThreatBig` predator-hunt of
the colonist (and only if the player's *Auto-pause on* option is at *Major threats* or lower); the mod
catches the earlier moment the hunter decides to disengage.

**Performance.** The watchdog is a registry of active hunters ticked from a `GameComponent`, not a
Harmony patch on every pawn's job driver. Registration piggy-backs on the cast-position prefix — the one
place that already identifies a managed hunt — so nothing runs for the thousands of non-hunting pawn
ticks in a large late-game colony. The only always-on cost is the master-switch check plus one empty-set
check per game tick. A registered hunter is re-checked every ~30 ticks (spread across pawns by
`IsHashIntervalTick`); each re-check is a cached solve plus two distance comparisons, and the bounded cell
scan only runs on the rare tick a hunter is actually unsafe. The engagement solve — the mod's heaviest
recurring op, since it reflects into CE and does a radial herd scan — is cached for ~4s, so the periodic
re-checks are almost all dictionary hits. The watchdog only reads state and (rarely) re-routes the pather
or ends the job, so it does not collide with CE. Toggle it with **Re-check the standoff during the
approach** (on by default).

Only `JobDefOf.Hunt` is affected. Predator hunting (`JobDefOf.PredatorHunt`), drafted combat, and CE
melee hunting all keep stock behaviour. The downed-prey melee-execute and corpse-collection phases of the
hunt job are skipped too — the re-check only governs the ranged approach.

---

## Bipod handling

If the equipped weapon has a `CombatExtended.BipodComp`, the hunter deploys it at the firing position
before shooting: CE's `BipodComp.DeployUpBipod()` raises the intent flag, then CE's own
`CE_JobDefOf.JobDef_SetUpBipod` job runs with `resumeCurJobAfterwards: true`, so the hunt job returns
once `CombatExtended.JobDriver_SetUpBipod` has waited `ticksToSetUp` and called `BipodComp.SetUpEnd`.
A per-pawn attempt counter prevents a deploy loop if setup keeps failing.

**This is not redundant with CE.** CE only ever auto-deploys from `BipodComp.SetUpStart`, which opens
with `if (!(pawn?.Drafted ?? false)) return;`, and the manual gizmo is likewise gated on `Drafted`.
A hunter on the Hunt work job is never drafted, so **stock CE will not deploy a bipod for a hunter
under any setting.**

### Caveat on what a bipod actually does

Reading `BipodComp.SetUpEnd` in CE 16.7.3.0, deploying changes exactly three things:

```csharp
changed.range      += Props.additionalrange;
changed.recoilAmount *= Props.recoilMulton;
changed.warmupTime   *= Props.warmupMult;
```

Sway is **not** touched — `CompProperties_BipodComp` declares `swayMult` and `swayPenalty`, but
`BipodComp` never reads them. Since CE's first-shot hit chance is driven by sway and spread, a
deployed bipod mainly helps **follow-up shots** (recoil) and **reach** (range); it does not directly
raise the first-shot hit chance the way the premise for this feature assumed. The engagement solver
accounts for the extra range, and the deploy-before-shot behaviour is implemented as specified — but
expect the accuracy benefit to show up across a burst rather than on shot one.

---

## Settings

| Setting | Default | Effect |
| --- | --- | --- |
| Enable smart hunter positioning | on | Master switch; off = stock CE behaviour |
| Easy-shot hit chance | 80% | Threshold fed to CE's solver |
| Tolerated revenge chance | 10% | `manhunterOnDamageChance` approached freely at or below this |
| Revenge standoff scale | 30 cells | Cells per full point of revenge chance above tolerance |
| Herd sensitivity | 1.0 | Cells of standoff per nearby herd-mate |
| Herd scan radius | 12 cells | Conspecific scan radius |
| Maximum standoff | 40 cells | Ceiling on the computed risk cap |
| Close in for one-shot kills | on | Allow breaching the standoff for a likely instant kill |
| Deploy CE bipod before shooting | on | |
| Re-check the standoff during the approach | on | Re-route to a safe cell en route; break off the hunt if the prey closes onto the hunter |
| &nbsp;&nbsp;↳ Pause and flag the pawn on break-off | on | On break-off, pause the game and jump a message to the hunter so you can micro |
| Debug logging | off | Logs easy range / risk cap / engagement range per hunt |

---

## Version support and verification

Everything above was verified against **CE `modVersion` 16.7.3.0** (`Development` and `master`, both
RimWorld 1.6) and against the **installed** `Assembly-CSharp.dll` — not against recollection. The
RimWorld side is enforced at compile time via `Krafs.Rimworld.Ref`; parameter names used by the
Harmony patches (`newReq`, `dest`) were confirmed by reflecting the shipped assembly.

**`About.xml` declares 1.6 only.** CE has no maintained 1.5 branch in the Continued repo, and the CE
1.5 tag ships **no `CE_Math.cs` at all** — the preferred solver does not exist there. Since this mod
hard-depends on CE, claiming 1.5 would only invite installs where it self-disables. Every CE member
is nonetheless reached by reflection (`CeBindings.cs`), with a documented fallback chain, so an older
CE release degrades rather than crashes:

1. `CE_Math.CalculateMaxDistance` — preferred, CE-authored.
2. Local binary search over `CE_Math.CalculateHitPercent` — mirrors CE's approach; **unverified on 1.5**.
3. Neither present → the mod logs a warning and stays inactive rather than misbehaving.

### Flagged as needing in-game verification

- **Bipod deploy sequence.** All names verified against CE source; the runtime interaction between
  CE's setup job and a *resumed* work job is not — that needs a live test.
- **One-hit-kill estimate.** Deliberately approximate; ignores CE's armour/ammo damage pipeline.
- **Lighting/weather.** CE's per-shot `ShiftVecReport` folds in lighting, weather and cover from the
  caster's actual position. `CalculateMaxDistance` uses a baked-in visibility constant instead, so at
  night or in bad weather the real hit chance will sit below the configured threshold.

---

## Building

Requires only the .NET SDK — RimWorld and CE reference assemblies come from NuGet, and CE is never
referenced at compile time.

```bash
dotnet build Source/BetterHunters/BetterHunters.csproj -c Release
```

Output goes straight to `Assemblies/BetterHunters.dll`.

---

## Workshop assets

`workshop-page/` holds the generated Steam Workshop art and copy:

| File | Use |
| --- | --- |
| `betterhunters-workshop-page.png` | The full infographic, 800×2077, 278 KB — host it and embed as one image |
| `betterhunters-NN-*.png` | The individual tiles, if you'd rather embed them separately |
| `Preview.png` | 640×360 title card — also copied to `About/Preview.png` for the in-game mod list |
| `workshop-thumbnail-512.png` | 512×512 square variant for the Workshop item thumbnail |
| `DESCRIPTION.bbcode` | Ready-to-paste Workshop description; swap in the hosted image URL |

Regenerate them with the included generator — it builds the panels as SVG and rasterises through
`sharp`, so the text is real font rendering rather than a screenshot:

```bash
cd workshop-page && npm install sharp && node build.js .
```

Edit the `blocks` array at the bottom of `build.js` to change the copy, or `ACCENT` at the top to
rebrand the whole page in one go.

## Not in v1

Post-shot behaviour is deliberately out of scope: no kiting, no retreat-on-manhunter, no
repositioning between shots. `EngagementSolver.TrySolve` is the hook to build that on.

## License

See [LICENSE](LICENSE).
