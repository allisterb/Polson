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
| What it should do (the brief's `- It should:`) and how it should feel | |
| The logo (the brief's `- Logo, in documents/:`), and any colours or brand guide in `documents/` — or none | |
| Images to include (the brief's `- Images to include, in documents/:`) — or none | |
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
- **The store's own pictures are used as given**, each with `Stage.place` (*The store's own pictures*, below): the logo
  is placed, never redrawn, recoloured or cut out, and its colours lead the palette; every image the brief lists
  appears. Read a brand guide with `Documents.ask` before choosing anything, and cite the file and page.
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

### What it is for, and how it should feel

The brief's `- It should:` line is the sign's goal, chosen by the director. **It decides what leads**, so read it
before the concepts and name it in each one:

| It should | What leads | What follows |
| :--- | :--- | :--- |
| **Draw people in** (the default) | what it sells, shown: the goods drawn, or the client's image large | the name beside it; the descriptor close to the name |
| **Help people find it** | the name as the shopfront shows it — the logo if there is one, its colours — as large as the safe area allows | little else: the descriptor small, an arrow only if the brief gives the store's direction |
| **Make the name remembered** | the name or logo on one strong field of the store's colour | at most the descriptor; no picture that competes with the name |

The brief's `- How it should feel:` line, if any, steers the palette, the typeface and the weight — never the words.
Record each choice it drove in `direction.md` with its source as `Brief: feel`, beside the evidence for it. With no
feel given, the category's look from the research decides, as it would anyway.

### The store's own pictures

The brief names them by role: `- Logo, in documents/:` (one file) and `- Images to include, in documents/:` (each must
appear). Anything else in `- Files supplied` is reference — a brand guide, a photo to read — and is placed only if the
brief asks. **Every supplied picture goes onto the sign with `Stage.place`**, which fits it without stretching and
records whether it will be sharp at the brief's size (`polson://sdk/core` under *Stage*). **A final render names any
supplied picture you have not placed**, and the run page shows it to the director.

**Look first**, in Stage 1, and write what you find in `brief.md`'s table:

```javascript
// A raster: its size, and its colours (the logo's lead the palette).
const logo = Skia.Image.load('documents/<the logo>');
log(`${logo.width} × ${logo.height} ${JSON.stringify(logo.palette(5))}`);
```

An `.svg` logo is vector and sharp at any size; render it to see it — `RenderSvg(file: 'documents/<the logo>', outFile:
'artifacts/00_logo.png')` — and read its colours off that render. Then ask what each picture is: `Video.ask(<the
picture>, 'Describe this picture: what it shows, its colours, any words in it, and what it sits on — transparent, a
white box, or a photograph. Where is its main subject, as fractions across and down?', { mimeType: 'image/png' })`
(`image/jpeg` for a JPG).

**The logo is fitted whole, never cropped** — `fit: 'contain'`, the default:

```javascript
const paper = Snap(1080, 1920);
const mark = Stage.place(paper, 'documents/<the logo>', { x: 140, y: 260, width: 800, height: 420 });
// mark.x, mark.y, mark.width, mark.height: where it landed in the box. Lay the next line out from there.
```

- **Clear space**: nothing else within a quarter of the logo's drawn height of it, on every side.
- **What it sits on — checked for you.** For a logo with a clear background, `Stage.place` holds its ink against what
  is already drawn behind it and records `placed against its ground: <file>`, failing under 3 : 1. **So draw the
  ground first, then place the logo.** Never take the ground's colour from the logo's own ink: francis_fashions_2
  (2026-11-18) sampled its black from a black wordmark and set it on that black, where it vanished. A dark logo goes
  on a light field or panel, or a version supplied for dark grounds is used. A ragged light fringe left by a cut-out
  shows on a dark ground and disappears on a light one. A logo on a white box or an opaque ground sits on a field of
  that ground's colour, or on a panel the box fills. **Never cut a logo's background out, recolour it, or redraw
  it**: that is a different logo.
- **A logo that spells the store's name is the name.** It becomes the dominant element, and the name is not set again
  in type. Its lettering is a line of the sign like any other, so it must pass the legibility check, **measured, not
  estimated**: `mark.lines` is each line of its lettering, top to bottom, with `px` its height on the deliverable, and
  `mark.smallestLine` the least of them. Add that to the check as a line of its own (below). A line's height is its
  ink, so an icon standing in a line makes that line taller than its letters: the smallest line is the one that
  counts. If it cannot be that large here — the sharpness check says how large the file allows — set the name in type
  as the dominant line and place the logo smaller, as the mark beside it.

**An image fills its region and is cropped around its subject** — `fit: 'cover'`, `focus` on the subject:

```javascript
const shop = Stage.place(paper, 'documents/<an image>', { x: 0, y: 1100, width: 1080, height: 820 },
  { fit: 'cover', focus: [0.5, 0.4] });              // the subject's fractions across and down, from looking
log(`kept ${(shop.kept * 100).toFixed(0)}% of the picture`);   // Stage.place warns under half
```

- **The region suits the picture's shape.** A landscape photograph on a portrait sign is a band across it, not a tall
  box it is cut down to a sliver for. `kept` says how much of the picture survives; under half, rethink the region.
- **Never crop off the product, a face, or words in the picture**, and never place a picture of a person without
  asking: the no-real-person rule is about pictures the studio makes, so the director confirms the client may show
  this one.
- A product cut out on a transparent ground is fitted whole, like a logo (`fit: 'contain'`).
- Words in a picture are the sign's words too: legible at the viewing distance, or small enough to read as texture.

**Sharpness is checked for you.** Each `Stage.place` records `placed sharp: <file>` — the source's pixels against
the pixels it covers on the deliverable, at the brief's size — and you cannot write it yourself. A failure says how
large the picture can go. **Draw it at that size or smaller; never keep the failure with `accepted`**: on a screen
read from metres away, an enlarged picture is the first thing that looks cheap. If no useful size is sharp, use the
picture as a softened background behind a colour field, or leave it out — and either way say which file needs a
larger version or a vector, in your report.

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
// A logo whose lettering is the name: its smallest line as Stage.place measured it, already in the deliverable's pixels.
if (typeof mark !== 'undefined' && mark.smallestLine) caps.push({ text: 'the logo\'s lettering', px: mark.smallestLine });
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
- Every supplied picture on every sign, placed with `Stage.place`, its `placed sharp` and `placed against its ground`
  checks passing — or named in your report with why it is not there.
- Every word on each final is the brief's: the server reads each `final_` file's words and fails `Client's words on …`
  for any it does not find in the brief ("collection", "new", "official" — a word you add is yours, not the store's).
- With several audiences, the set side by side in `artifacts/set.png`, each labelled — one sign several times, only
  the words moved.
- A loop for the screen is a separate request (the motion workflow builds a film on a poster found in `documents/`):
  say so in `findings.md` rather than making one here.
