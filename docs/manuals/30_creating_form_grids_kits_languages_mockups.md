# Studio Manual 30: Creating Form — Grids, Kits, Languages and Mock-ups

> **Credits & Theoretical Foundation**: Distilled from Ellen Lupton (ed.), *Graphic Design Thinking: Beyond Brainstorming* (Princeton Architectural Press, 2011), chapter 3, *How to Create Form* — its opening (p. 113) for §1, *Alternative Grids* (pp. 120–125) for §2, *Kit of Parts* (pp. 126–131, after Andrew Blauvelt's Walker Art Center identity, with the HUSH studio's animation kits) for §3, *Brand Languages* (pp. 132–135) for §4, and *Mock-Ups* (pp. 136–138) for §5 — and from John Colette & Austin Shaw, *Motion Design Toolkit* (Routledge, 2023), ch. 12, for design systems in motion. Ledgered 2026-10-24; methods in our words, no passage reproduced. Lupton points to Carsten Nicolai's *Grid Index* (2009) and Marty Neumeier's *The Brand Gap* (2005); neither is in `reference/`, and both are cited through her. **Where a rule is ours rather than theirs, it says so.**
> **Purpose**: How a chosen concept becomes form — by a grid found in the world rather than a column grid, by a small kit of parts recombined, by a language that holds across everything the work appears on — and how to see the result where it will live before calling it done.

---

## 1. What This Is For

> **Implemented by**: nothing directly. This is the stage between the chosen concept (Manual 29) and the comps.

A concept is tested when it becomes visible: how it works, what it says, and whether it means what was intended. The
answers often send a designer back to the concept, and that is the method working, not failing (p. 113). Lupton makes
two further points that this manual is built on:

- **Form-making can come first.** Shapes, colours and surfaces explored openly can turn up the concept, not only carry
  it. A kit of parts (§3) and a found grid (§2) are both ways of making form before you know exactly what it will say.
- **A good concept executed dully is lost.** Two designers will make one concept into two different pieces; the
  difference is in the making. Deliberate methods help the making as much as they help the thinking.

Four methods, in two pairs. **The alternative grid and the kit of parts generate form**; **the brand language makes it a
system** that holds across everything it appears on, and **the mock-up tests it in the world** — at its size, on its
surface, among its neighbours.

---

## 2. Alternative Grids

> **Implemented by**: `Layout.linesFrom(image, { into, count })`, `Layout.tessellation(rect, { type, size })` and
> `Layout.lines(rect, lines)`, each returning a `grid`; `grid.along`, `grid.snap`, `grid.pathData`, `grid.draw`, and
> **`grid.fit`**, which measures whether the piece was organised by it.

A column grid is for efficiency: a newspaper, a website, a book whose pages must agree. An alternative grid is for
exploring form. It is made of lines at any angle, found in an everyday object or image, or made out of the information
itself, and its job is to open possibilities of layout and type rather than to make them efficient (p. 120).

### 2a. The method: observe, replicate, organise (p. 121)

1. **Observe.** Find a structure in the world: a city, a building, a tree, the weather, rock, a work of art. Lupton's
   cases: a tornado cut from paper and photographed, its lines carrying angled type, with a rainbow border nodding to
   *The Wizard of Oz*; the street map of a novel's setting holding
   text about the novel; a face holding text about a monograph (pp. 121–123).
2. **Replicate.** Photograph or sketch it, then find the linear structure in the picture: abstract it into a network of
   lines across the whole page.
3. **Organise.** Set the type and the elements by the lines — and use the lines to **cut, crop, distort and overlap**
   as well. The grid becomes a tool for play rather than for rational construction.

**Tessellations** are a second source: a pattern of tiles covering a plane with no gaps gives several grids from one set
of lines — triangles, diamonds, hexagons, the rhombille's cubes (pp. 124–125).

### 2b. How it works here

- **Observe something tied to the subject, never a picture of another work.** The object from the mind map's trunk or
  the brief: the thing itself, in its place. A licensed photograph (`Photo.of`), your own sketch of it, or a map you
  draw from what the brief gives. **Qloo's pictures stay research** (Manual 29 §2b): a grid traced from someone else's
  poster is their composition.
- **Replicate with `Layout.linesFrom(picture, { into: page, count })`.** It finds the picture's dominant straight lines
  and lays them across the page as the photograph would be cropped onto it. Look at the result with
  `grid.draw(ctx)` over the picture: keep **six to twelve lines**; a grid of thirty is noise. If the picture's lines are
  not the ones that matter, give them by hand with `Layout.lines(page, [...])`.
- **Organise with the grid's own calls.** `grid.along(i, t)` gives a point and an angle on line `i`: translate there and
  rotate by the angle to set a line of type on it. On a paper, a `<textPath>` on `grid.pathData(i)` does the same.
  Clip a picture to a tessellation's `cells`, crop at a line, let a shape cross a line and change colour where it does.
- **Keep the hierarchy readable** (Manual 11, ours): angled display type is the point of the method; **small text stays
  level**, on a level line of the grid where one exists, because a reader turns a page but not a sentence.

### 2c. The check (ours)

A grid drawn over a page laid out by columns decorates it. Measure what it organised:

```js
const fit = grid.fit([titleBaseline, taglineBaseline, imageBox, creditsBox]);
Stage.check('the alternative grid organised the piece, not decorated it', fit.share >= 0.6, fit.summary);
```

Pass the **baselines** of the display lines (`{ x1, y1, x2, y2 }`, which `grid.along` already gave you) and the boxes of
the main elements (`getBBox()`). A box is on the grid when an edge lies along a line or its centre sits on a crossing.

### 2d. In motion

The lines are paths. **Write the grid on** as a reveal (`comp.outline({ path: grid.pathData(i) })`, `trimEnd` keyed
0 → 1), **send type along a line** rather than in from the side, and **move the camera along one** (Manual 25 §15).
The grid is a good place for the motion identity's geometry (Manual 25 §5j): one angle, used every time something enters.

### 2e. When to use it

As **a fourth route to a concept**, beside the verb, the figure and the sign (Manual 29 §3–§5): *a grid from an object*,
where the concept names the object and why it belongs to the subject. It is not compulsory. It suits a film, an event,
a book — anything with a place or a thing at its heart — and suits least a piece whose information needs a reader's
patience, where a column grid serves the reader better.

---

## 3. Kit of Parts

> **Implemented by**: a `KIT` at the top of the piece's script; the kit sheet, `artifacts/03_kit.png`; a judged
> `Stage.check`. For a set across audiences, the kit section of `direction.md`.

Andrew Blauvelt's identity for the Walker Art Center was not a logotype but **an open system**: a set of ornamental
marks typed like a typeface, combined into endless variations, with new marks added as the programme needed them — "a
living visual brand" where a traditional identity is a closed set of fixed rules. Lupton generalises it: a way of
developing **a series of genetically related forms** (p. 126). Blauvelt calls it designs for making designs.

The method is two steps (p. 127):

1. **Create the parts.** Built, drawn, photographed, or sampled from the culture. A small number of elements.
2. **Reconfigure.** Decide how they combine. A simple vocabulary gives a rich syntax: a square and a square cut by an
   arc became an alphabet, then figures and patterns; leaves, bullets and arrowheads, rotated and repeated, became
   flowers (pp. 127–129).

The HUSH studio builds **kits for animation** the same way: a palette, textures, shapes and illustration parts, from
which complex films are made (pp. 130–131). Colette and Shaw describe the same move: designing not an image but a
process that makes images inside a considered framework (ch. 12).

### 3a. Every piece has a kit (ours; the director's decision, 2026-10-25)

Before the comps, write the kit in the script:

```js
// The kit: every element of the piece is one of these, or several of them combined.
const KIT = {
    palette: { ground: '#16242b', lead: '#2f8c8c', accent: '#de5a28', ink: '#f2e8d5' },   // in order of prominence
    type: { display: 'Georgia', text: 'Arial', sizes: [120, 48, 18] },
    weights: [2, 8, 24],                                                                  // three line weights
    parts: {
        disc: (ctx, x, y, r, colour) => { /* … */ },
        groove: (ctx, x, y, r, w) => { /* … */ },
        bar: (ctx, x, y, w, colour) => { /* … */ },
    },
};
```

- **Four to eight parts**: shapes from the concept, two or three line weights, a texture (drawn, or `Assets.material`),
  the palette in its order, the type and its sizes. In a film, add **the motion identity** (Manual 25 §5j: one ease, three
  durations, one entrance) and the moves that recur.
- **Then reconfigure on a sheet**, `artifacts/03_kit.png`: the parts in a row, then **at least six reconfigurations** —
  repeat, rotate, scale, overlap, crop, combine — each labelled with the rule that made it. The comps are chosen from
  these, or the sheet shows that the kit is too poor and the parts change.
- **Everything in the piece comes from the kit.** An element drawn outside it either joins the kit, named, or goes.
  Record it as a judged check: `Stage.check('every element is drawn from the kit', outside.length === 0, …)`, naming
  any that are not.

### 3b. In motion

A film's beats are **reconfigurations of one kit** (HUSH): the parts recur, change scale, combine, become each other.
A transition can be one part turning into the next beat's part. The kit is also what makes a second film, a cut-down
or another audience's version cheap: the same parts, reconfigured.

### 3c. A set across audiences is a kit with fixed and variable parts

*(This was Manual 29 §7.)* For a set made for several audiences, decide before any piece is made **what is fixed** —
the type, the hierarchy, the figures and their order, the end card's structure, and in motion the motion identity — and
**what each audience may change** — palette temperature, pace, the hero image, the allusion (Manual 29 §4). Each piece is
then the kit reconfigured, and the set reads as one family whose members differ for a reason. Write the kit as its own
section of `direction.md`, before the audiences' sections: the fixed parts, the variable parts, and for each variable
part the evidence that set it per audience.

---

## 4. Brand Languages

> **Implemented by**: the language section of `direction.md` (or the kit's, for a set); `dos.md`, measured by
> `Stage.borders`; `Stage.mockup` for its vehicles; `Logo.generateBrandPresentationSheet` for the logo's sheet.

A brand language is more than a logo: **a system** of colour, shape, image, type, texture, pattern and materials that
carries an organisation's values to a particular audience. Its look, feel and behaviour build associations over time —
Lupton's examples are a jeweller's blue box, a restaurant's arches, a carrier's brown trucks — and a new one draws on
the communicative power of the elements and the culture's references (p. 132).

Five steps (p. 133):

1. **Define the audience.** Who receives the language and lives with it: age, life, education. Here that is the
   measured profile (Manual 29 §2).
2. **Make a vocabulary** — verbal and visual — that can be carried by the vehicles that deliver the messages. That is
   the kit (§3), with the words: the tone, the names, a line or two.
3. **Decide the order.** Which element is most prominent and which least, and by what: position, scale, colour,
   composition.
4. **Apply it systematically**, to vehicles — packages, signs, uniforms, tags — and find out how rigid or flexible the
   rules are. **Push it**: enlarge the scale, wrap round corners, bleed off the edges.
5. **Document the family**, whether as a concept presentation or as a manual for the people who will use it.

### 4a. How it works here

- **When**: the logo workflow (a mark is delivered with the small language around it, as Lupton's FAB case is — a mark
  of modular parts, applied to stationery, apparel, signs, pp. 134–135), the `product` types, and any set across
  audiences. A one-off poster or film needs its kit (§3), not a language.
- **The vocabulary and the order** go in `direction.md`, each with its evidence as every choice there is.
- **The rules are `dos.md`**, measured where they can be (Manual 25 §13c, `Stage.borders`): the palette, the faces,
  the clear space, what the mark may sit on.
- **Apply it** with mock-ups (§5) on three or four vehicles chosen for this brand — a card, a sign, a package, a screen
  — and **push it once on purpose**: one application that enlarges the mark past the edge, crops it, or wraps it round
  a corner, to find where the rules bend and where they break. Say which in `direction.md`.
- **Document the family** in the pitch book (`Stage.book`): the vocabulary, the order, the rules, the vehicles.

---

## 5. Mock-Ups

> **Implemented by**: `Stage.mockup(path, { design, setting, size })`, drawn settings `wall`, `frame`, `shelter`,
> `billboard`, `phone`, `screen`, `card` and `box`; or `onto` a picture with `corners` and `find`.

A mock-up simulates how a design will look, feel and behave once produced: built in three dimensions, or drawn, or
montaged. It is a tool for exploring scale, shape, surface and hierarchy; for testing materials, proportions and
construction; and for showing the client (p. 136). For a package Lupton's method is to **plan** a flat net (a real box
flattened and traced), **design** each face so it reads the right way up once folded, and **produce** it: print, score,
fold and build (p. 137).

### 5a. What a mock-up is for here

- **Scale.** A poster is judged at its size from where it is seen. `wall` and `frame` stand a figure 1.7 m tall beside
  it at its real size (`size: 'A2'`, `'A0'`, `{ width, height }` in millimetres), and `shelter` and `billboard` set it
  in its street. Look before you finish: does the headline hold at that size, and does the image? This is the poster
  workflow's two reading distances made visible to the client.
- **Surface and behaviour.** A film's still or its frames on a `phone` in a feed or a public `screen`; a mark on a `card`
  and a `box` (`side` and `top` its other faces — design them flat, the right way up, as Lupton's net asks).
- **The client.** The pitch book shows the work in use: one or two mock-ups after the piece itself.

### 5b. A plate built around a panel

For a richer setting, generate one: draw the panel where the work should go as a black silhouette on `#808080`, **at the
design's own shape**, pass it as `conditionOn` to `Assets.backdrop` with a descriptor asking for the panel *blank and
evenly lit*, then

```js
Stage.mockup('artifacts/mockup_shelter_a.png', { design: poster, onto: plate, corners, find: true });
```

**`find: true` is required**, and was measured: a plate built around a shelter panel's quad put the shelter where the
quad was but drew its blank panel **inset and about two thirds as wide**. Pinned to the quad the poster floated over the
scene; pinned to the panel the plate drew, found near the corners given, it sat in it. A warning says when the panel
found is another shape than the design. One requisition a plate; reuse it for every audience's version.

### 5c. Honesty (ours)

- **Every mock-up is labelled as one**, on the picture, and **filed as one**: `Stage.mockup` refuses a file name without
  `mockup` in it, and records it as a sheet, never as the piece.
- **No real place's actual signage, no real brand's property, no person's likeness** in a setting. The drawn figure is a
  silhouette for scale. Under the real-film rules, no studio key art in the setting either.
- **A mock-up is not evidence that the work was placed anywhere**, and `direction.md` never says otherwise.

### 5d. In motion

A film is shown where it will play. A still on its `phone` or `screen` for the pitch book; for a short loop, put each
frame through `Stage.mockup(null, { design: frame, setting: 'phone' })` and encode the `image`s with `Motion.frame(...)`,
about 0.1 s a frame. Keep the loop to the film's opening seconds, which is what a feed shows.

---

## 6. Where Each Belongs in a Run

1. **Concepts** (Manual 29) — and, where it is the route, **the grid from an object** (§2) as one of them.
2. **The kit** (§3), drawn and reconfigured, with the chosen concept and before any comp. In a film, before the board.
3. **The comps or the board**, built from the kit, organised by the grid where there is one, `grid.fit` recorded.
4. **The language** (§4) for a logo, a product or a set: vocabulary, order, rules, the vehicles and one push.
5. **Mock-ups** (§5) at delivery, labelled, into the pitch book after the piece itself.

---

## 7. One Script, End to End

A kit of three parts, a tessellation's grid organising them, the check that it did, and the poster on a wall at A2.

```javascript
// The kit: a palette in order, the type, three parts. Every element below is one of them.
const KIT = {
    palette: { ground: '#16242b', lead: '#2f8c8c', accent: '#de5a28', ink: '#f2e8d5' },
    parts: {
        disc: (c, x, y, r) => { c.fillStyle = KIT.palette.lead; c.beginPath(); c.arc(x, y, r, 0, Math.PI * 2); c.fill(); },
        bar: (c, x, y, w, angle) => { c.save(); c.translate(x, y); c.rotate(angle); c.fillStyle = KIT.palette.accent; c.fillRect(0, -6, w, 12); c.restore(); },
        word: (c, text, at, size) => {
            c.save(); c.translate(at.x, at.y); c.rotate(at.angle * Math.PI / 180);
            c.fillStyle = KIT.palette.ink; c.font = `700 ${size}px Georgia`; c.fillText(text, 0, 0); c.restore();
        },
    },
};

// The grid: a triangle tessellation over the page, turned a little.
const W = 700, H = 990;
const poster = createCanvas(W, H), c = poster.getContext('2d');
c.fillStyle = KIT.palette.ground; c.fillRect(0, 0, W, H);
const grid = Layout.tessellation(Layout.rect(0, 0, W, H), { type: 'triangle', size: 160, rotate: 12 });
grid.draw(c, { color: KIT.palette.lead, opacity: 0.18 });

// Organise by it: the disc on a crossing, the bar and the title along lines.
const centre = grid.snap({ x: W / 2, y: H * 0.4 }, { to: 'intersection' });
KIT.parts.disc(c, centre.x, centre.y, 170);
// The longest of the near-level lines: a short one is a corner of the page.
const level = grid.lines.map((l, i) => ({ l, i })).filter(o => Math.abs(Math.sin(o.l.angle * Math.PI / 180)) < 0.4)
    .reduce((best, o) => o.l.length > best.l.length ? o : best).i;
const line = grid.nearest({ x: W / 2, y: H * 0.8 }).line;
const start = grid.along(line, 0.12);
KIT.parts.word(c, 'BE COOL', start, 96);
c.font = '700 96px Georgia';
const end = grid.along(line, 0.12 + c.measureText('BE COOL').width / grid.lines[line].length);
const bar = grid.along(level, 0.25);
KIT.parts.bar(c, bar.x, bar.y, 260, bar.angle * Math.PI / 180);

const fit = grid.fit([{ x1: start.x, y1: start.y, x2: end.x, y2: end.y }, { x: centre.x, y: centre.y }]);
Stage.check('the grid organised the poster', fit.share >= 0.6, fit.summary);
log(grid.summary + ' ' + fit.summary);

// And the poster where it will live: on a wall, at A2, beside a figure for scale.
const m = Stage.mockup(null, { design: poster, setting: 'wall', size: 'A2', width: 900, height: 700 });
m.image;
```
