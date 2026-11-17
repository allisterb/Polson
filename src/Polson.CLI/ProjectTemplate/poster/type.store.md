## The type: store

**A sign for one store, shown on a digital screen in a mall** — a freestanding display (a *totem*) or a screen on a
wall — and a version of it for each audience the brief names, in its own language. It says three things at most: **the
store's name, what it sells, and one message if the brief gives one.** It is read in a glance by someone walking past,
from metres away, so it is designed for that and checked for it. It is not a logo: with no logo supplied, the name is
set in type for this sign, and a mark is a separate request (the logo workflow).

Everything below **replaces the print parts of the process**: there is no print file, no bleed and no paper size.
The deliverable is the picture at the screen's pixels.

### What the brief gives

Fill the brief table in `brief.md` with these rows in place of the film ones, and ask for what is missing:

| Parameter | Value |
| :--- | :--- |
| The store's name, exactly as it appears | |
| What it sells, in the brief's words (*shoe store*, *children's books*) | |
| The message, worded exactly as it may appear — or none | |
| Logo and colours in `documents/` (file names), or none | |
| The screen: pixels and orientation (the brief's `- Sizes:`), and its diagonal in inches | |
| Viewing distance: how far away most people read it | |
| Audiences A, B, … — place, age band, language | |
| Anything explicitly ruled out | |

**Q2 for a sign is the screen and the distance.** With no diagonal, propose **a 55" panel** (the usual freestanding
display, 685 × 1218 mm in portrait); with no distance, propose **5 m** — each *assumed* in `direction.md` until the
director answers.

### The rules of the sign

- **Words come only from the brief.** The name, what it sells, and the message, each as the brief words it. **No offer,
  percentage, price, date, "sale", "new", "now open", "hurry" or "limited"** unless the brief gives it in those words:
  an invented offer on a shop's sign is a promise the shop did not make, and in most places against the law.
- **The store's own assets are used as given.** A logo in `documents/` is placed, not redrawn or recoloured; its colours
  lead the palette. Read a brand guide with `Documents.ask` before choosing anything, and cite the file and page.
- **What it sells is shown, never a brand's product.** Imagery of the goods — a shoe, a stack of books — is drawn, or
  one material from `Assets` (`polson://manual/16`), with **no logo, mark, stitching pattern or shape that reads as a
  real brand's**. The store may stock that brand; the sign does not advertise it.
- **No real person**, as on every poster here.
- **The store is the client.** The sign is for the name in the brief and no other business.

### The key messages — before anything is designed

As for a product (Lupton, *How to Refine a Creative Brief*): a sign's first job is that a passer-by knows **what kind
of store it is**. A name alone rarely says it. `direction.md` opens with the table:

| Key message | Words as they appear | Where on the sign | Checked by |
| :--- | :--- | :--- | :--- |
| **What it sells** | two to four of the brief's words | beside the name, smaller | read-back, source `brief`; the cold viewing |
| The store's name | as the brief gives it | the dominant element | read-back |
| The message (if any) | the brief's words exactly | | read-back, source `brief` |

- **What it sells is a descriptor, not a sentence**: two to four words lifted whole from the brief, one run of
  them, with no full stop — from "A shoe store that sells urban styled sneakers", *urban styled sneakers* or
  *sneakers*. Lifted, so
  `Verify.image` still finds it with source `brief`; a paraphrase comes back as `own words`. francis_fashions
  (2026-11-17) set the brief's whole sentence over three lines, nearly the weight of the name, and a passer-by reads one
  line in passing, not three.
- **It is set below the name in weight or size**, and never takes the name's line: the name leads, what it sells
  answers it.

**Q4 for a sign** is which of the three a passer-by takes if they take one — almost always the name and what it
sells together.

**The cold viewing**, once each sign is final, as for a product but asking what the store sells:

```javascript
const seen = await Video.ask('artifacts/final_a.png',
  'This sign is in a shopping mall. What kind of store is it for? Answer in a few words.', { mimeType: 'image/png' });
log(seen.success ? seen.text : seen.error);
```

Record the answer word for word, then `Stage.check('a passer-by can say what the store sells', <your reading>,
seen.text)`. If it names another kind of store, make what it sells larger or nearer the name, and ask again. Never ask
a leading question.

### The evidence

Stage 2 for a sign is the brief and the documents first — what the store sells, who shops there, where the screen
stands — then **one `Research` run** planned as a product's, about **the category**: how stores of this kind present
themselves on signage and shopfronts, the words shoppers use for them, and, for each audience's place, anything that
would misfire there. What comes back is the category's look, cited as that, never one store's trade dress to borrow.

### Making it, on a screen

- **Build the paper at the screen's pixels**: `Snap(1080, 1920)` for a 1080 × 1920 screen, so one paper unit is one
  pixel of the sign. For a 2160 × 3840 screen build at `Snap(1080, 1920)` and render at the brief's size
  (`width: 2160, height: 3840`): the drawing is vector, and halving the units keeps the numbers readable.
- **Every comp writes the picture and the vector**, no print file:

  ```
  ExecuteScript(scriptFile: 'poster.js', outFile: 'artifacts/04_comp_a.png', outSvg: 'artifacts/04_comp_a.svg')
  ```

  The print checks in *The checks* (bleed, ppi at printed size, safe margin in millimetres) do not apply. The rest do.
- **Keep everything that must be read inside 90% of the screen**, centred — a cabinet's lip and a screen's edge
  hide the outer pixels, as a broadcast's title-safe area does. Background and imagery may run to the edges.
- **One dominant element**: the name, with what it sells beside it. The message, if any, third and smaller. A
  direction arrow only if the brief gives the store's direction from where the screen stands.
- **A sign is lit and seen for hours.** Value contrast does the work, as on a poster: 4.5 : 1 for every line below
  display size, 3 : 1 at display size (`Motion.contrast`).

### Type size from the viewing distance

**The smallest line must be legible from the brief's distance**, and that is measured, not guessed. The rule is the
**2010 ADA Standards, Table 703.5.5** (US Department of Justice; public domain), for characters 1015–1780 mm above the
floor — where a totem's screen is: **at least 16 mm (5/8 in) of capital height under 1830 mm (6 ft) of viewing distance,
plus 3.2 mm (1/8 in) for each 305 mm (1 ft) beyond it**. A store's name and a directory are exempt from the ADA's sign
rules (§216.1, exception 1), so it is cited here as the best public rule of thumb for size, not as compliance: say
exactly that in `direction.md`.

```javascript
// The screen and the distance, from the brief (Q2) or assumed.
const SCREEN = { diagonalIn: 55, w: 1080, h: 1920 }, DISTANCE_M = 5;
// One pixel in millimetres on that panel, and the smallest capital height the rule allows, in pixels.
const pxPerMm = Math.hypot(SCREEN.w, SCREEN.h) / (SCREEN.diagonalIn * 25.4);
const feet = DISTANCE_M * 1000 / 304.8;
const minCapPx = (16 + Math.max(0, feet - 6) * 3.175) * pxPerMm;

// Cap height as a share of the font size: the ink of an "H", which measureText gives as its actual ascent (never
// getBBox's height, which is the face's whole line box). Faces differ by up to a fifth at one size, so measure each.
const capShare = (family, weight = 'normal') => {
  const x = createCanvas(10, 10).getContext('2d');
  x.font = `${weight} 100px ${family}`;
  return x.measureText('H').actualBoundingBoxAscent / 100;
};
```

Then, for every line on the sign, its capital height in pixels is `fontSize × capShare(family, weight)` × the scale the
paper renders at (1, or 2 for a 2160-wide screen built at 1080). **Record the smallest**:

```javascript
// LINES: each line of type as set — { text, size, family, weight } — kept beside the drawing that sets it.
const SCALE = 1;                                          // 2 for a 2160-wide screen built at 1080
const caps = LINES.map(l => ({ text: l.text, px: l.size * capShare(l.family, l.weight) * SCALE }));
const smallest = caps.reduce((a, b) => (b.px < a.px ? b : a));
Stage.check('every line is legible from the viewing distance', smallest.px >= minCapPx,
  `smallest cap ${smallest.px.toFixed(0)} px ("${smallest.text}"); the rule asks ${minCapPx.toFixed(0)} px at ` +
  `${DISTANCE_M} m on a ${SCREEN.diagonalIn}" panel`);
```

A line that fails is made larger or cut — **never kept** with `accepted`: a line nobody can read from where they
stand is not on the sign.
For 1080 × 1920 on a 55" panel, the rule asks about 77 px at 5 m and 95 px at 6 m.

### In its setting

**The mock-up is the totem**: `Stage.mockup('artifacts/mockup_totem_a.png', { design: paper, setting: 'totem', size: {
height: <the panel's height in mm> } })` — a 55" panel in portrait is 1218 mm tall, which is the default. Look at it
beside the figure: does the name hold, and does what it sells read before you look closely? For a screen on a wall,
`'wall'` with the panel's size. It replaces check 6's settings; the other checks stand.

### direction.md

**The key messages first** (the table above, with the cold viewing's answer for each sign). Then the screen and the
distance, each Brief or Assumed, and the computed minimum cap height beside the smallest line's. Then the design: its
three concepts, and every choice with its evidence. Then **one section per audience** for what moves.

### Before you finish — for a sign

- The final sign per audience: `artifacts/final_a.png` and `.svg` at the brief's pixels — **no `.pdf`**, unless the
  brief's formats ask for one. The brief's size is the deliverable's: a `final_` file at another size comes back with
  a warning, which is a fault to fix.
- One totem mock-up per final, labelled as a mock-up as `Stage.mockup` draws it. A mock-up is never evidence that the
  sign was placed anywhere.
- With several audiences, the set side by side in `artifacts/set.png`, each labelled — one sign several times, only
  the words moved.
- A loop for the screen is a separate request (the motion workflow builds a film on a poster found in `documents/`):
  say so in `findings.md` rather than making one here.
