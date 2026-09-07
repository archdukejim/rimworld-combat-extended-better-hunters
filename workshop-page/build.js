'use strict';
// Workshop infographic generator for "Better Hunters (Combat Extended)".
// Renders VE-style dark panels as SVG, rasterises via sharp, then stacks them into one tall image.
//   npm install sharp && node build.js ./out
// SHARP_PATH can point at an existing sharp install instead of a local one.
const fs = require('fs');
const path = require('path');
let sharp;
for (const p of [process.env.SHARP_PATH, 'sharp'].filter(Boolean)) {
  try { sharp = require(p); break; } catch (e) { /* try next */ }
}
if (!sharp) {
  console.error("Could not load 'sharp'. Run: npm install sharp   (or set SHARP_PATH)");
  process.exit(1);
}

const OUT = process.argv[2] || path.join(__dirname, 'out');
fs.mkdirSync(OUT, { recursive: true });

// ---- palette ---------------------------------------------------------------
const ACCENT = '#c8873a';
const BG = '#17181a';
const PANEL = '#232527';
const PANEL_EDGE = '#34373b';
const TITLE = '#f0ece3';
const BODY = '#b6b2a8';
const MUTED = '#8b877d';
const FONT = 'Segoe UI, Arial, sans-serif';
const W = 800;

// ---- crude text metrics for wrapping --------------------------------------
const NARROW = "iljI!.,;:'|`";
const THIN = 'ft()[]{}/\\-r';
const WIDE = 'mwMW@';
function charW(c, size) {
  let f = 0.52;
  if (NARROW.includes(c)) f = 0.27;
  else if (THIN.includes(c)) f = 0.35;
  else if (WIDE.includes(c)) f = 0.83;
  else if (c === ' ') f = 0.26;
  else if (c >= 'A' && c <= 'Z') f = 0.63;
  else if (c >= '0' && c <= '9') f = 0.55;
  return f * size;
}
function textW(s, size, bold) {
  let w = 0;
  for (const c of s) w += charW(c, size);
  return bold ? w * 1.06 : w;
}
function wrap(text, size, maxW, bold) {
  const out = [];
  for (const para of String(text).split('\n')) {
    let line = '';
    for (const word of para.split(/\s+/).filter(Boolean)) {
      const trial = line ? line + ' ' + word : word;
      if (textW(trial, size, bold) > maxW && line) { out.push(line); line = word; }
      else line = trial;
    }
    if (line) out.push(line);
  }
  return out;
}
const esc = (s) => String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;');

// ---- glyphs (drawn to fit a 100x100 box, stroked in accent) ---------------
const GLYPHS = {
  reticle: `<g fill="none" stroke="${ACCENT}" stroke-width="5" stroke-linecap="round">
      <circle cx="50" cy="50" r="30"/><circle cx="50" cy="50" r="12"/>
      <path d="M50 6 v16 M50 78 v16 M6 50 h16 M78 50 h16"/>
      <circle cx="50" cy="50" r="3.5" fill="${ACCENT}" stroke="none"/></g>`,
  standoff: `<g fill="none" stroke="${ACCENT}" stroke-width="5" stroke-linecap="round">
      <circle cx="34" cy="50" r="9" fill="${ACCENT}" stroke="none"/>
      <path d="M62 20 a34 34 0 0 1 0 60" stroke-dasharray="7 8"/>
      <path d="M76 12 a48 48 0 0 1 0 76" opacity="0.45"/>
      <path d="M46 50 h22 M60 42 l9 8 -9 8"/></g>`,
  bipod: `<g fill="none" stroke="${ACCENT}" stroke-width="5" stroke-linecap="round" stroke-linejoin="round">
      <path d="M10 34 h74 l8 6 -8 6 H10 z" fill="${ACCENT}" fill-opacity="0.18"/>
      <path d="M46 46 L26 88 M46 46 L66 88"/><path d="M20 88 h14 M58 88 h14"/></g>`,
  sliders: `<g fill="none" stroke="${ACCENT}" stroke-width="5" stroke-linecap="round">
      <path d="M12 26 h76 M12 50 h76 M12 74 h76"/>
      <circle cx="32" cy="26" r="8.5" fill="${BG}"/><circle cx="62" cy="50" r="8.5" fill="${BG}"/>
      <circle cx="26" cy="74" r="8.5" fill="${BG}"/></g>`,
  check: `<g fill="none" stroke="${ACCENT}" stroke-width="5" stroke-linecap="round" stroke-linejoin="round">
      <circle cx="50" cy="50" r="36"/><path d="M32 51 l13 13 24 -28"/></g>`,
  // A shot from the left is stopped by a wall; a colonist stands safely behind it.
  backdrop: `<g fill="none" stroke="${ACCENT}" stroke-width="5" stroke-linecap="round" stroke-linejoin="round">
      <path d="M6 44 h30"/><path d="M30 37 l9 7 -9 7"/>
      <path d="M48 18 v52 M60 18 v52"/>
      <path d="M48 33 h12 M48 46 h12 M48 59 h12" opacity="0.45"/>
      <circle cx="80" cy="40" r="8.5"/><path d="M67 72 a13 13 0 0 1 26 0"/></g>`,
};

// ---- header ribbon ---------------------------------------------------------
function headerSvg(title) {
  const h = 74, rh = 46, y = (h - rh) / 2, notch = 22, m = 6;
  const pts = `${m + notch},${y} ${W - m - notch},${y} ${W - m},${y + rh / 2} ${W - m - notch},${y + rh} ${m + notch},${y + rh} ${m},${y + rh / 2}`;
  const size = 23;
  const t = esc(title.toUpperCase());
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${h}">
    <rect width="${W}" height="${h}" fill="${BG}"/>
    <polygon points="${pts}" fill="${ACCENT}"/>
    <text x="${W / 2}" y="${y + rh / 2 + size * 0.35}" text-anchor="middle" font-family="${FONT}"
      font-size="${size}" font-weight="700" letter-spacing="2.5" fill="#1a1409">${t}</text>
  </svg>`;
}

// ---- feature panel ---------------------------------------------------------
function featureSvg(b) {
  const PAD = 22, TILE = 158, GAP = 20;
  const cx = PAD + TILE + GAP;            // content panel x
  const cw = W - cx - PAD;                // content panel width
  const inX = 20;                         // inner padding of content panel
  const tw = cw - inX * 2;                // text width

  const SUB = 21, FLAV = 16.5, BODYS = 15.5, ROWS = 15;
  const flavLines = b.flavor ? wrap(b.flavor, FLAV, tw, false) : [];
  const bodyLines = b.body ? wrap(b.body, BODYS, tw, false) : [];
  const rows = b.rows || [];

  // measure content height
  let ch = 18;                             // top pad
  ch += SUB + 12;                          // subtitle ribbon
  if (flavLines.length) ch += flavLines.length * (FLAV + 6) + 8;
  if (bodyLines.length) ch += bodyLines.length * (BODYS + 7) + 6;
  if (rows.length) ch += 10 + rows.length * 26;
  ch += 18;                                // bottom pad

  const tileBlockH = TILE + (b.requirements ? 34 : 0);
  const h = Math.max(ch + PAD * 2, tileBlockH + PAD * 2);
  const cy = (h - ch) / 2;                 // vertically centre the content panel
  const ty = (h - tileBlockH) / 2;

  let s = `<svg xmlns="http://www.w3.org/2000/svg" width="${W}" height="${h}">
    <rect width="${W}" height="${h}" fill="${BG}"/>`;

  // icon tile
  s += `<rect x="${PAD}" y="${ty}" width="${TILE}" height="${TILE}" rx="10" fill="${PANEL}" stroke="${ACCENT}" stroke-width="2"/>`;
  const g = GLYPHS[b.glyph] || GLYPHS.check;
  s += `<g transform="translate(${PAD + (TILE - 100) / 2}, ${ty + (TILE - 100) / 2})">${g}</g>`;

  if (b.requirements) {
    s += `<rect x="${PAD}" y="${ty + TILE + 8}" width="${TILE}" height="26" rx="5" fill="${PANEL}" stroke="${PANEL_EDGE}"/>
      <text x="${PAD + TILE / 2}" y="${ty + TILE + 26}" text-anchor="middle" font-family="${FONT}"
        font-size="11.5" fill="${MUTED}">Requires: <tspan fill="${ACCENT}" font-weight="600">${esc(b.requirements)}</tspan></text>`;
  }

  // content panel
  s += `<rect x="${cx}" y="${cy}" width="${cw}" height="${ch}" rx="8" fill="${PANEL}" stroke="${PANEL_EDGE}"/>`;

  let y = cy + 18;
  // subtitle with accent tab
  s += `<rect x="${cx + inX}" y="${y - 2}" width="5" height="${SUB + 4}" rx="2.5" fill="${ACCENT}"/>`;
  s += `<text x="${cx + inX + 14}" y="${y + SUB * 0.78}" font-family="${FONT}" font-size="${SUB}"
      font-weight="700" fill="${TITLE}">${esc(b.title)}</text>`;
  y += SUB + 12;

  for (const l of flavLines) {
    s += `<text x="${cx + inX}" y="${y + FLAV * 0.8}" font-family="${FONT}" font-size="${FLAV}"
        font-style="italic" fill="${ACCENT}">${esc(l)}</text>`;
    y += FLAV + 6;
  }
  if (flavLines.length) y += 8;

  for (const l of bodyLines) {
    s += `<text x="${cx + inX}" y="${y + BODYS * 0.8}" font-family="${FONT}" font-size="${BODYS}"
        fill="${BODY}">${esc(l)}</text>`;
    y += BODYS + 7;
  }
  if (bodyLines.length) y += 6;

  if (rows.length) {
    y += 10;
    for (const r of rows) {
      s += `<circle cx="${cx + inX + 3}" cy="${y + 7}" r="3" fill="${ACCENT}"/>`;
      s += `<text x="${cx + inX + 14}" y="${y + 12}" font-family="${FONT}" font-size="${ROWS}"
          font-weight="600" fill="${TITLE}">${esc(r.k)}</text>`;
      const kw = textW(r.k, ROWS, true);
      s += `<text x="${cx + inX + 14 + kw + 10}" y="${y + 12}" font-family="${FONT}" font-size="${ROWS}"
          fill="${MUTED}">${esc(r.v)}</text>`;
      y += 26;
    }
  }

  return s + '</svg>';
}

// ---- preview / thumbnail card ---------------------------------------------
function previewSvg(w, h) {
  const cxp = w / 2;
  return `<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}">
    <defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0%" stop-color="#1e2023"/><stop offset="100%" stop-color="#121315"/></linearGradient></defs>
    <rect width="${w}" height="${h}" fill="url(#g)"/>
    <rect x="0" y="0" width="${w}" height="7" fill="${ACCENT}"/>
    <rect x="0" y="${h - 7}" width="${w}" height="7" fill="${ACCENT}"/>
    <g transform="translate(${cxp - 52}, 44) scale(1.04)">${GLYPHS.reticle}</g>
    <text x="${cxp}" y="${h * 0.545}" text-anchor="middle" font-family="${FONT}" font-size="46"
      font-weight="700" letter-spacing="1" fill="${TITLE}">BETTER HUNTERS</text>
    <text x="${cxp}" y="${h * 0.545 + 36}" text-anchor="middle" font-family="${FONT}" font-size="20"
      font-weight="600" letter-spacing="3.5" fill="${ACCENT}">COMBAT EXTENDED</text>
    <text x="${cxp}" y="${h * 0.545 + 74}" text-anchor="middle" font-family="${FONT}" font-size="17"
      font-style="italic" fill="${MUTED}">Close the distance. Take the shot that lands.</text>
  </svg>`;
}

// ---- page definition -------------------------------------------------------
const blocks = [
  { type: 'header', title: 'Better Hunters \u00b7 Combat Extended' },
  {
    type: 'feature', glyph: 'reticle', title: 'Close to the shot that lands',
    requirements: 'Combat Extended',
    flavor: "A wounded animal runs. A clean kill doesn't.",
    body: "Stock CE hunters fire the instant prey enters weapon range. Ranges are long, CE's hit chance falls off hard with distance, and the shot usually just wounds \u2014 so the animal flees, or turns on the hunter. Better Hunters walks the hunter in until Combat Extended's own hit-chance solver says the first shot is a high-probability kill, then fires. Not one cell closer than it needs to be.",
    rows: [
      { k: 'Hit chance from', v: "CE's own solver, not a guess" },
      { k: 'Default threshold', v: '80% on the first shot' },
      { k: 'Decision point', v: 'the hunt cast position' },
    ],
  },
  {
    type: 'feature', glyph: 'standoff', title: 'Never closer than safe',
    flavor: 'Some things shoot back.',
    body: "Before closing, the hunter works out a safety standoff from how likely and how bad retaliation would be: the animal's revenge chance, its body size, whether it is a predator, and how many of its herd are standing nearby. A lone hare gets walked up on. A muffalo herd does not. If no easy shot exists outside that standoff, the hunter holds and takes the worse shot rather than starting a fight \u2014 unless the round should drop the animal outright.",
    rows: [
      { k: 'Revenge risk', v: 'manhunter-on-damage chance' },
      { k: 'Danger scaling', v: 'body size and predator flag' },
      { k: 'Herd pressure', v: 'live scan for nearby kin' },
      { k: 'Standoff ceiling', v: '40 cells, configurable' },
    ],
  },
  {
    type: 'feature', glyph: 'backdrop', title: 'Never a colonist in the backdrop',
    flavor: "Know your target — and what's behind it.",
    body: "A stray shot doesn't vanish when it misses — in Combat Extended it keeps flying past the prey until a wall stops it. So before firing, the hunter checks the line of fire beyond the target. It will never take a shot with a colonist, pet or other friendly downrange: it shifts to a clear angle, and if none exists it calls off the hunt and flags it. Your own buildings are kept clear too, whenever a cleaner angle is free.",
    rows: [
      { k: 'Hard rule', v: 'no friendly pawn behind the prey' },
      { k: 'On a blocked shot', v: 'reposition, or call it off' },
      { k: 'Buildings', v: 'avoided when a clear angle exists' },
      { k: 'Ideal backstop', v: 'a solid wall' },
    ],
  },
  {
    type: 'feature', glyph: 'bipod', title: 'Bipod up before the trigger',
    flavor: 'Set up, then shoot. Not the other way round.',
    body: "If the hunting weapon carries a CE bipod, the hunter deploys it at the firing position before taking the shot, then carries straight on with the hunt. Stock CE never does this for a hunter \u2014 its bipod auto-setup only runs for drafted pawns, and a colonist on hunting duty never is. Deploying extends the weapon's reach and steadies follow-up shots.",
    rows: [
      { k: 'Runs', v: "CE's own bipod setup job" },
      { k: 'Then', v: 'resumes the hunt automatically' },
      { k: 'Gains', v: 'extra range, tighter recoil' },
    ],
  },
  { type: 'header', title: 'Tune it to your colony' },
  {
    type: 'feature', glyph: 'sliders', title: 'Every number is a slider',
    flavor: 'Cautious trapper or reckless sharpshooter \u2014 your call.',
    body: 'Raise the easy-shot threshold to make hunters close further before firing. Raise the tolerated revenge chance to make them bolder around dangerous game. Turn herd sensitivity up if manhunter packs have burned you before. A master switch returns everything to stock CE behaviour instantly.',
    rows: [
      { k: 'Easy-shot hit chance', v: '30% to 99%' },
      { k: 'Tolerated revenge chance', v: '0% to 100%' },
      { k: 'Herd sensitivity and radius', v: 'per-species scan' },
      { k: 'Maximum standoff', v: 'up to 80 cells' },
      { k: 'Master on / off', v: 'instant revert to stock CE' },
    ],
  },
  { type: 'header', title: 'Requirements \u00b7 Compatibility' },
  {
    type: 'feature', glyph: 'check', title: 'What you need',
    requirements: 'Combat Extended',
    flavor: 'Behaviour only. Nothing to migrate.',
    body: 'A hard dependency on Combat Extended \u2014 this mod does nothing without it. It adds no defs and saves no data of its own, so you can add it to a running colony or pull it out again without breaking anything. Only the colonist hunting job is touched: predator hunting, drafted combat and CE melee hunting behave exactly as before.',
    rows: [
      { k: 'Requires', v: 'Combat Extended' },
      { k: 'RimWorld', v: '1.6' },
      { k: 'Save safe', v: 'add or remove any time' },
      { k: 'Touches', v: 'the hunting job only' },
    ],
  },
];

(async () => {
  const tiles = [];
  for (let i = 0; i < blocks.length; i++) {
    const b = blocks[i];
    const svg = b.type === 'header' ? headerSvg(b.title) : featureSvg(b);
    const name = `betterhunters-${String(i + 1).padStart(2, '0')}-${b.type}.png`;
    const file = path.join(OUT, name);
    await sharp(Buffer.from(svg, 'utf8')).png().toFile(file);
    const meta = await sharp(file).metadata();
    tiles.push({ file, h: meta.height });
    console.log(`tile ${name}  ${meta.width}x${meta.height}`);
  }

  // stack into one tall page image
  const total = tiles.reduce((a, t) => a + t.h, 0);
  let top = 0;
  const composites = tiles.map((t) => { const c = { input: t.file, left: 0, top }; top += t.h; return c; });
  const merged = path.join(OUT, 'betterhunters-workshop-page.png');
  await sharp({ create: { width: W, height: total, channels: 4, background: BG } })
    .composite(composites).png({ compressionLevel: 9 }).toFile(merged);
  console.log(`merged  ${W}x${total}  ${(fs.statSync(merged).size / 1024).toFixed(0)} KB`);

  // preview / thumbnail cards
  const prev = path.join(OUT, 'Preview.png');
  await sharp(Buffer.from(previewSvg(640, 360), 'utf8')).png().toFile(prev);
  console.log('preview 640x360');
  const sq = path.join(OUT, 'workshop-thumbnail-512.png');
  await sharp(Buffer.from(previewSvg(512, 512), 'utf8')).png().toFile(sq);
  console.log('thumb   512x512');
})().catch((e) => { console.error('FAIL', e); process.exit(1); });
