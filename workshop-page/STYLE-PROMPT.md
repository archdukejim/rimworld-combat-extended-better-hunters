# Workshop infographic style prompt

Paste the block below into a render tool (or an LLM updating one) to reproduce this visual system.
Every number is taken from the working generator in `build.js`, not invented.

---

Render Steam Workshop description panels as SVG at 800 px wide, then rasterise to PNG. Text must be
real font rendering, never a screenshot. Panel heights are derived from content — never fixed.

## Design tokens

Single swappable brand token: `ACCENT`. Everything else is a fixed neutral ramp.

    ACCENT      #c8873a   brand; the only chromatic colour on the page
    BG          #17181a   page background
    PANEL       #232527   panel and tile fill
    PANEL_EDGE  #34373b   1 px panel stroke
    TITLE       #f0ece3   headings and row labels
    BODY        #b6b2a8   body copy
    MUTED       #8b877d   row values, chip text
    ON_ACCENT   #1a1409   text sitting on an accent fill (warm near-black, not pure black)

    font-family: Segoe UI, Arial, sans-serif

## Rule 1 — the accent marks structure, never prose

Accent is allowed on: ribbon fills, the subtitle tab, row bullets, glyph strokes, tile borders, the
flavor line, and the value half of a "Requires" chip. It is never used for body text. If you find
yourself accenting a paragraph to add emphasis, restructure into a row instead. One hue, used
sparingly, is what makes the page read as a system rather than decoration.

## Rule 2 — three type roles, no more

    subtitle   21 px / weight 700 / TITLE
    flavor     16.5 px / italic / ACCENT     one short line per panel, the only italic on the page
    body       15.5 px / regular / BODY      one paragraph, roughly 40-60 words
    rows       15 px    bold TITLE label + regular MUTED value on the same line

## Component: header ribbon

Tile 800x74. A flat notched hexagon 46 px tall, centred vertically (y = 14), 6 px side margin,
22 px notch inset:

    points = (m+notch, y) (W-m-notch, y) (W-m, y+rh/2) (W-m-notch, y+rh) (m+notch, y+rh) (m, y+rh/2)

Fill ACCENT. Title uppercase, 23 px, weight 700, letter-spacing 2.5, centred, fill ON_ACCENT.
Headers are section dividers only — no body copy, no icons.

## Component: feature panel

Geometry: outer pad 22, tile 158x158, gap 20 between tile and content panel.
Content panel x = 200, width = 578, inner pad 20, so wrap width = 538.

Vertical rhythm inside the content panel:

    top pad                18
    subtitle               + 12 after
    each flavor line       + 6      (block adds + 8 after)
    each body line         + 7      (block adds + 6 after)
    rows                   + 10 before block, 26 px pitch
    bottom pad             18

Panel height = that sum. Overall tile height = max(contentH + 44, tileBlockH + 44), where
tileBlockH = 158 plus 34 if a Requires chip is present.

**The tile and the content panel are centred vertically and independently of each other.** This is
the detail that keeps a page of varying-length panels from looking ragged — do not top-align them.

Parts:
- Icon tile: rounded rect rx 10, fill PANEL, 2 px ACCENT stroke, glyph centred in a 100x100 box.
- Requires chip (optional): directly under the tile, same width, 26 px tall, rx 5, fill PANEL,
  PANEL_EDGE stroke. Text 11.5 px, "Requires: " in MUTED then the value in ACCENT weight 600.
  Keep at 11.5 px — 12.5 px makes a two-word dependency touch the box edges.
- Content panel: rounded rect rx 8, fill PANEL, PANEL_EDGE stroke.
- Subtitle tab: 5 px wide accent bar, rx 2.5, height subtitle+4, at the panel's inner left edge;
  subtitle text starts 14 px right of it.
- Rows: 3 px ACCENT bullet, label 14 px right of it, value 10 px after the measured label width.
  Maximum 5 rows. A row is a fact, not a sentence: "Default threshold = 80% on the first shot".

## Component: preview / thumbnail card

Vertical gradient #1e2023 to #121315. Full-width 7 px ACCENT bars flush top and bottom. Centred
glyph above the wordmark. Title 46 px weight 700 TITLE; subtitle 20 px weight 600 letter-spacing 3.5
ACCENT; tagline 17 px italic MUTED. Render at 640x360 for the in-game mod list and 512x512 for the
Workshop thumbnail from the same source.

## Glyphs

Draw them. Do not use emoji, stock icon sets, or clip art. Each glyph is monoline vector art in a
100x100 box: `stroke = ACCENT`, `stroke-width 5`, round caps and joins, `fill="none"`. Permitted
exceptions: a small solid accent dot as a focal point, and a `fill-opacity 0.18` accent wash to
suggest mass.

Each glyph must literally depict its feature rather than gesture at the category — a crosshair for
targeting, a dot with radiating arcs for standoff distance, a barrel on splayed legs for a bipod,
three knobbed tracks for settings. A generic gear or checkmark is the fallback of last resort.

## Copy voice

Concrete and declarative. No marketing adjectives ("powerful", "seamless", "immersive"), no
exclamation marks, no second-person hype. Em-dash asides are welcome. State what the thing does and
what it costs. Where the mod contradicts a common assumption, say so plainly in the body — that is
the most persuasive sentence on the page.

Panel titles are short imperative or declarative phrases, sentence case: "Never closer than safe",
"Bipod up before the trigger". Flavor lines are one short sentence with a turn in it.

## Text wrapping

SVG has no auto-wrap. Wrap manually with a per-character advance estimate as a fraction of font
size, then emit one `<text>` per line:

    narrow  i l j I ! . , ; : ' | `     0.27
    thin    f t ( ) [ ] { } / \ - r     0.35
    wide    m w M W @                   0.83
    space                               0.26
    A-Z                                 0.63
    0-9                                 0.55
    default                             0.52
    bold: multiply the total by 1.06

Measure label widths the same way when placing a row's value after its label.

## Output

Render each block to its own PNG, then stack them seamlessly (gap 0, background BG) into one tall
image so the description embeds a single hosted URL. PNG compression level 9. Keep the merged image
under 700 KB; split into chunks if it exceeds that. Emit the individual tiles too, for pages that
want per-section embeds.
