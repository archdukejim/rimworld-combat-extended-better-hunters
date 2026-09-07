# Better Hunters (CE) — Player's Guide

Better Hunters changes how your colonists position themselves when hunting under **Combat
Extended**. It does nothing on its own — CE is a hard requirement. It touches the colonist
**Hunt** work job only; predator hunting, drafted combat, and CE melee hunting are untouched.

It adds no defs and stores no save data, so it is safe to add to or remove from an existing
colony at any time.

---

## Engagement positioning

Stock CE hunters fire the moment an animal enters weapon range. Ranges are long and CE's hit
chance falls off hard with distance, so the first shots usually just wound the animal — which
then flees or turns on the hunter.

Better Hunters instead walks the hunter in until Combat Extended's *own* hit-chance math says
the first shot clears your **Easy-shot hit chance** threshold, then stops and fires. It never
closes more than it needs to: once the shot is easy, the hunter holds there.

## Safety standoff

The hunter will not close past a safety distance ("risk cap") worked out from how likely and
how bad retaliation would be:

- The animal's **revenge chance** (its chance to go manhunter when hurt), above your tolerance.
- Its **body size**, and whether it is a **predator** (predators count double).
- How many of its **herd-mates** of the same species are standing nearby.

A docile animal with no revenge chance has a standoff of zero — the hunter closes freely. A
dangerous one is kept at arm's length.

## When safe and easy conflict

If no easy shot exists *outside* the safety distance, the hunter holds at the safety distance
and takes the worse shot rather than starting a fight — **unless** the round should drop the
animal in a single hit. A clean one-shot kill provokes no revenge, so in that case the hunter
closes in and takes it.

## Approach re-check

The safe distance is chosen against where the animal is *now*, but animals move. Better Hunters
keeps re-checking the standoff for the whole approach, not just at the start:

- **While walking in** — if the animal drifts toward the spot the hunter was heading to and that
  spot is now too close, the hunter quietly re-routes to a fresh safe cell.
- **Once in position** — if the animal closes the distance *onto* the hunter (the rare case of a
  predator turning to hunt your colonist), the hunter **breaks off the hunt** and disengages
  rather than shooting point blank.

## Pause and flag on break-off

When a hunter breaks off, the game pauses and a message jumps to the pawn, so you can step in
and take over. This only fires for genuinely dangerous animals — a harmless animal wandering
close never triggers it — and it is throttled to at most once every ~42 seconds per hunter so a
chase can't spam it. It complements RimWorld's own alert, which only fires once an animal fully
escalates to hunting your colonist.

You can turn this off and leave just the break-off behaviour.

## Shot backdrop safety

A hunter also checks what is behind the animal before it fires. In Combat Extended a missed
shot does not vanish — it keeps flying past the target until a wall stops it — so a shot taken
with something valuable downrange can hit it by accident.

Before firing, and again continuously as it approaches, the hunter traces the line of fire past
the prey:

- **A friendly pawn behind the prey is a hard stop.** Colonists, pets, prisoners, visitors and
  allies are never fired past. The hunter moves to a clear angle instead; if the prey drifts, or
  a colonist wanders downrange, it repositions on the fly.
- **If no clear angle exists at all**, the hunter calls off the hunt: it removes the hunt mark
  from the animal — so no colonist keeps re-attempting the unsafe shot — and raises an alert
  telling you why. Re-mark it once the area is clear.
- **Your own buildings** are kept out of the line of fire too, but only softly: the hunter
  prefers an angle with nothing of yours behind the prey, yet will still take a shot over a
  building when that is the only angle available. A solid wall is the ideal backstop.

Wild animals and hostiles behind the prey are not protected — only things you would not want shot.

## CE bipod deploy

If the hunting weapon has a Combat Extended bipod, the hunter sets it up at the firing position
before shooting, then continues the hunt. Stock CE never does this for a hunter — its bipod
auto-setup only runs for drafted pawns, and a colonist on hunting duty never is.

---

## Settings

Open **Options → Mod settings → Better Hunters (CE)**.

| Setting | Default | What it does |
| --- | --- | --- |
| Enable smart hunter positioning | on | Master switch. Off = stock CE hunter behaviour. |
| Easy-shot hit chance | 80% | How likely the first shot must be before the hunter fires. Higher = closes further in. |
| Tolerated revenge chance | 10% | Animals at or below this manhunter-on-damage chance are approached freely; above it, the hunter keeps its distance. |
| Revenge standoff scale | 30 cells | Extra standoff per full point of revenge chance above the tolerance (before size/predator scaling). |
| Herd sensitivity | 1.0 | Extra standoff per nearby herd-mate of the same species. |
| Herd scan radius | 12 cells | How far around the prey to look for herd-mates. |
| Maximum standoff | 40 cells | Hard ceiling on the computed safety distance. |
| Close in when the shot should drop it outright | on | Allow closing inside the safety distance when the round should kill in one hit. |
| Deploy CE bipod before shooting | on | Set up a fitted CE bipod at the firing position first. |
| Re-check the standoff during the approach | on | Re-route to a safe cell if the prey drifts; break off if it closes onto the hunter. |
| ↳ Pause and flag the pawn on break-off | on | On break-off, pause the game and jump a message to the hunter so you can micro. |
| Check the shot's backdrop | on | Never fire with a colonist, pet or other friendly behind the prey; reposition, or call off the hunt if there's no clear angle. |
| ↳ Backdrop scan distance | 20 cells | How far past the prey to look for friendlies and buildings. The scan stops at the first wall. |
| ↳ Also keep the shot clear of your buildings | on | Prefer angles with no building of yours behind the prey — only when a cleaner angle is free; never cancels a shot. |
| Debug logging | off | Logs the computed ranges and decisions for each hunt (for troubleshooting). |

---

## Compatibility

- **RimWorld 1.6**, requires **Combat Extended** (hard dependency).
- Does not patch anything CE itself patches — no known conflicts.
- Behaviour-only: no new defs, no saved data; add or remove mid-save freely.
