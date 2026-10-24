# Studio Manual 29: Concepts — Verbs, Figures, Signs and the Kit

> **Credits & Theoretical Foundation**: §8 also follows L. Galli's slide template of BJ Fogg's "Conceptual Designs" (in B. Laurel, ed., *Design Research*, MIT Press, 2003), ledgered 2026-10-24. Distilled from Ellen Lupton (ed.), *Graphic Design Thinking: Beyond Brainstorming* (Princeton Architectural Press, 2011) — *Creative Brief* (pp. 56–59) for §2, *Action Verbs* (pp. 74–77, after Alex F. Osborn's *Applied Imagination*, 1953, and Koberg & Bagnall's *The Universal Traveler*, 1972) for §3, *Rhetorical Figures* (pp. 82–87, after Ehses & Lupton's *Design Papers: Rhetorical Handbook*, 1988) for §4, *Icon, Index, Symbol* (pp. 88–91, after Peirce) for §5, *Lost in Translation* (pp. 104–107, Toormix's method) for §6, and *Kit of Parts* (pp. 126–131) and *Brand Languages* (pp. 132–135) for §7 — and from John Colette & Austin Shaw, *Motion Design Toolkit* (Routledge, 2023), ch. 1 for the resolve and ch. 12 for design systems. Both ledgered 2026-10-24; methods in our words, no passage reproduced. **Where a rule is ours rather than theirs, it says so.** The sources Lupton cites (Osborn, Koberg & Bagnall, Ehses & Lupton, Peirce) are not in `reference/` and are cited through her.
> **Purpose**: How to get from a brief to a concept worth drawing — several, made by different methods, before one is chosen — and, for a set made for several audiences, how to make the set one system rather than several separate pieces.

---

## 1. What This Is For

> **Implemented by**: nothing. This is the stage before the first script.

The first idea is usually the obvious one, and the obvious one is not wrong: a cliché is common ground, and Lupton's
methods start from it on purpose. What goes wrong is stopping there. A studio generates several ideas, discards most
of them, and expects the problem itself to shift as it works; the first idea is rarely the last (*Introduction*).

So: **narrow the brief to a sentence (§2), then make at least three concepts by different methods** — a verb applied to
the core image (§3), a figure (§4), a sign chosen for its kind (§5) — and choose between them. For one piece that is the
whole of it. For a set across audiences, §6 governs the words and §7 the system.

---

## 2. The Brief in One Sentence

> **Implemented by**: the brief table in `brief.md`.

A creative brief is a checkpoint, not a form: the work is judged against it as it goes (*Creative Brief*, pp. 56–57).
Lupton's refinement ends in two things worth writing down before any concept:

- **One sentence** that says what the piece is for and what makes it this piece and no other.
- **The key messages** — the few ideas it must carry, in order.

A concept that serves neither is decoration, however good. Put the sentence at the top of `direction.md`, and test each
concept against it before choosing.

---

## 2a. The Brain Dump: Many Small Sketches, Forced Together

> **Implemented by**: the drawing tools you already have — shapes, silhouettes, type — on one sheet of small cells, and
> `bitmap.diff` to find the cells that are the same idea twice.

The generic design is the first idea, refined. The cure Lupton gives first in her chapter on getting ideas is volume
before judgement (*Visual Brain Dumping*, pp. 62–63, after Luba Lukova): with the purpose defined, make **many small,
quick drawings — at least twenty in twenty minutes — several to a page so they can be compared; keep moving, drawing
another view of an idea rather than erasing and refining one; then review and choose a few to pursue.** Lukova's
strongest posters come from two ideas colliding into a third meaning, and the collision is what the sketching hunts for.

**Feed it with forced connections** (pp. 68–69, after Koberg & Bagnall): make two lists and force links between them —
a service with a style, a function with a form. Here the two lists are (ours) **the brief's subject and its elements**,
and **the audience's distinctive titles and tags from `Taste`**. Each cell forces one pair together and is labelled with
it in a few words. The ideas then come from this audience's measured taste rather than from what any brief about the
subject would produce, which is the point of measuring the audience at all.

**Rules for the sheet** (ours, from the method):

- **Sixteen to twenty-four cells**, crude: tone blocks, the dominant shape, a word or two of type. **No cell gets a second
  pass in this stage** — refinement is what the stage exists to postpone.
- **One idea per cell.** A cell recoloured, recropped or nudged is the same idea; the sheet is measured for that (below).
- **Label each cell** with its pairing — *"cold brew × slow-cinema stillness"* — so the review can say what each idea is.
- **Review on the sheet**: mark the three or four worth pursuing, and park the rest by name in `concepts.md` and a
  `Stage.note`, so a later run can find them with `Recall` — Lukova keeps her unused sketches for exactly that (p. 62).
- **Then make the three concepts** (§3–§5) from the chosen cells, and write them up (§8).

```javascript
// A brain-dump sheet: twelve crude cells, each a forced pairing, then measured for near-duplicates with bitmap.diff.
const PAIRS = ['cab × film noir rain', 'cab × chess', 'cab × clock', 'cab × swarm', 'cab × keyhole', 'cab × stopwatch',
    'cab × chess (recoloured)', 'cab × lighthouse', 'cab × medal', 'cab × river', 'cab × pocket', 'cab × crown'];
const CW = 200, CH = 150, sheet = createCanvas(4 * CW, 3 * CH + 40), out = sheet.getContext('2d');
out.fillStyle = '#f2efe8'; out.fillRect(0, 0, sheet.width, sheet.height);

// Each cell is drawn by one function, in its colours or as a black silhouette: the silhouette is what is compared.
function drawCell(ctx, i, silhouette) {
    const k = i === 6 ? 1 : i;                                       // cell 6 repeats cell 1's idea in another colour
    ctx.fillStyle = '#ffffff'; ctx.fillRect(0, 0, CW - 10, CH - 30);
    ctx.fillStyle = silhouette ? '#000000' : (i === 6 ? '#3a6ea5' : '#15151a');
    ctx.fillRect(20 + (k * 37) % 90, 25 + (k * 23) % 50, 40 + (k * 13) % 60, 30 + (k * 17) % 40);
    ctx.beginPath(); ctx.arc(130 - (k * 19) % 60, 70 - (k * 11) % 30, 12 + (k * 7) % 20, 0, Math.PI * 2); ctx.fill();
}
const shapes = PAIRS.map((label, i) => {
    const cell = createCanvas(CW - 10, CH - 30); drawCell(cell.getContext('2d'), i, false);
    out.drawImage(cell, (i % 4) * CW + 5, Math.floor(i / 4) * CH + 5);
    out.fillStyle = '#15151a'; out.font = '12px Georgia';
    out.fillText(label, (i % 4) * CW + 8, Math.floor(i / 4) * CH + CH - 10);
    const mono = createCanvas(CW - 10, CH - 30); drawCell(mono.getContext('2d'), i, true);
    return mono.toBitmap();
});
// Keep moving, measured: two cells whose silhouettes match are one idea, whatever their colours.
const twins = [];
for (let a = 0; a < shapes.length; a++) for (let b = a + 1; b < shapes.length; b++)
    if (shapes[a].diff(shapes[b]).similarity >= 0.97) twins.push(`${PAIRS[a]} = ${PAIRS[b]}`);
out.fillStyle = '#9b2c2c'; out.font = '14px Georgia';
out.fillText(twins.length ? `same idea twice: ${twins.join('; ')}` : 'every cell a different idea', 8, sheet.height - 14);
sheet;
```

The check compares silhouettes, not colours: a cell recoloured is the commonest way a sheet repeats itself. Two
different ideas can share a silhouette by chance — a found pair is looked at, not deleted on sight.

## 3. Verbs: Activating an Idea

> **Implemented by**: in motion, every verb below is a move — `scale`, `offset`, `angle`, a group's `timeOffset`, a region's points.

Osborn's method, as Koberg and Bagnall extended it and Lupton presents it (pp. 74–75): take the core image or idea and
**apply verbs to it**, one after another, sketching each quickly and judging none until the list is done. Each verb
forces a visible, structural change, so even a tired image — a target for performance, a reaper for death — turns into
something with a story in it. Then look back, find the two or three that give an old idea a new turn, and take them
further.

The verbs she lists and adds: **magnify, minify, multiply, rearrange, reverse, invert, combine, substitute, adapt,
modify, flatten, stretch, thicken, melt, dissect, explode, shatter, squeeze, bend, soften, wring, frame.**

**In motion the verb is the move** (ours): the still sketch shows the result, the film shows the verb happening.

| Verb | On a still | As motion here |
| :--- | :--- | :--- |
| magnify / minify | the object too big or too small for its setting | `scale` keyed through the change; the setting holds still (Manual 25 §5, staging) |
| multiply | many where one was | duplicate and offset: one group reused with `timeOffset`, or a staggered set |
| rearrange / reverse / invert | the parts out of their usual order, flipped | `offset` and `angle` keyed; a passage keyed again in reverse order |
| combine / substitute | two things made one; one standing in for another | two shapes crossing into one, a cut on the moment they meet |
| dissect / explode | the parts separated, labelled | an exploded view, parts moving apart along their own axes and back |
| flatten / stretch / squeeze | the form under force | squash and stretch with its volume kept (Manual 25 §5) |
| melt / soften / bend | the rigid made pliable | a region's points keyed to new places |

Record in `direction.md` the core image, the verbs tried, and which one the concept uses.

---

## 4. Figures: Rhetoric for Pictures

> **Implemented by**: nothing directly. A figure is a decision about what to show.

Rhetoric's figures of speech depart from ordinary statement to make a point land; applied to images, objects and
layouts they do the same, and they make a reliable way to generate a concept instead of reaching for the literal
answer (pp. 82–85). The poster workflow already draws on a short list (Laserow & Delgado; Lupton's *How Posters Work*).
These are the ones that most often make a picture:

| Figure | What it does to a picture | e.g. (ours) |
| :--- | :--- | :--- |
| **Metaphor** | shows one thing as another that shares a quality | a market as a field still unploughed |
| **Metonymy** | names a thing by what goes with it | a car service as a set of keys |
| **Synecdoche** | a part for the whole | a wheel for the car, a seat for the ride |
| **Ellipsis** | leaves out what context supplies | passengers at ease, the seats left undrawn (Lupton's Acela example, p. 86) |
| **Antithesis** | opposites set in parallel | two bars, one figure the other's sixteenth |
| **Hyperbole** | exaggerates for emphasis | an absurdly tall stack of cab receipts |
| **Litotes** | understates | a single coin where a fortune is meant |
| **Personification** | gives a thing a person's qualities | a bar that strains to rise |
| **Repetition** | the same form, again, until it means something | one cab, then forty |
| **Allusion** | points to a person, place or work the viewer knows | a frame composed after a known film |
| **Paradox** | sets something against its own use | a chair covered in spikes (p. 85) |

**Two guards, both ours.**

- **Figures act on pictures and words, never on figures.** A hyperbolic picture is rhetoric; a hyperbolic bar is a false
  chart. An antithesis of two true numbers on one scale is fine — it is the comparison the data makes — but no figure
  may change a quantity's size, scale or sign (Manual 13 §2 binds in every frame).
- **An allusion only works on viewers who know the reference.** For an audience the brief names, choose the allusion
  from what that audience is measured to watch and love (`Taste`), not from what the studio knows; record the titles it
  rests on. An allusion the audience does not share is an obscure picture.

---

## 5. Signs: Icon, Index, Symbol

> **Implemented by**: nothing directly. A sign is chosen for its kind before it is drawn.

Peirce's three kinds of sign, as Lupton applies them to marks and icon sets (pp. 88–89):

- **An icon resembles** what it stands for: a drawing of a dog.
- **An index points to it, or is a trace of it**: the bark, the bowl, the lead on its hook; smoke for fire; an arrow
  for a direction. Lupton calls the index the kind that most often gives the intriguing solution.
- **A symbol is arbitrary**: the word *dog*, the letters that spell it.

Most real signs mix the kinds — her lotus mark for a Thai herbal foundation reads as a flower (icon), holds a tree and a
leaf in its negative shapes, and suggests a meditating figure (p. 90). Choosing the kind on purpose is how a concept
gets a second level.

**For a picture made of facts** (ours): in a pitch or a report, an index is often the honest picture — an empty seat for
a cab's idle hours, a meter stopped — **provided the document states the fact it traces**. An index of something the
document does not say is an invented claim.

**Across cultures.** Lupton notes that even icons rest on cultural convention, to varying degrees (p. 89). Our rule
following from it: **a symbol is the most culture-bound choice** — colours with meanings, gestures, religious and
national marks — so for a set made for several audiences, prefer icons and indexes, and check every symbol against each
audience before it carries meaning. Manual 13's colour research and the report type's red-and-green rule are cases of
exactly this.

---

## 6. Words Across Audiences

> **Implemented by**: `Locale.number` for figures; nothing for words, which is why this section exists.

Toormix design in three languages at once, and treat the words as part of the design research rather than a
translation step at the end (pp. 104–105). Their method, with what it means here:

1. **Name the languages and places first.** One multilingual place, or several places? The brief's audiences answer it.
2. **Write first in the language you know best**, and keep it plain: **no slang, no colloquial phrases, no rhymes, no
   puns** — wordplay is the first thing lost, and a pun (paronomasia) almost never survives.
3. **Words differ in force.** Lupton's examples include a mild English vulgarity whose Spanish equivalent is far
   harsher (p. 107). A word's register is part of its meaning.
4. **Prefer what needs no translation**: place names, years, numbers, and words that share a root across the languages.
5. **A currency sign does not travel.** A `$` reads as a dollar where it is the dollar and as something else elsewhere
   (p. 107). Write money as `Locale.number` writes it for the place — `US$` where the local `$` is another currency —
   and never convert it.
6. **Test with native speakers.** We cannot (ours): a model's reading of a translation is not a native speaker's.
   Record every translated line in `direction.md` as **not checked by a native speaker**, keep the claim and the call to
   action short enough to check, and leave brand and product names untranslated.

---

## 7. A Set Is a Kit

> **Implemented by**: one `build(audience)` in the workflow's script, the films or pieces made from it.

Andrew Blauvelt's *kit of parts* for the Walker Art Center was an open system rather than a fixed logo: a set of
elements, and ways to recombine them, producing endless related variations — "designs for making designs"
(pp. 126–127). Colette and Shaw describe the same move in motion: designing not an image but a process that makes
images within a considered framework (ch. 12). It is what a set made for several audiences should be.

Lupton's steps for a brand language (p. 133), as they apply to a set:

1. **Define the audience** — here, each audience, measured.
2. **Make a vocabulary**: type, palette rules, shapes, the motion vocabulary (eases, moves, transitions).
3. **Decide the order**: what is most prominent, what least, by position, scale and colour.
4. **Apply it systematically** to every piece, and find how far it bends — scale, cropping, bleeding off the edge.
5. **Document the family.**

**For a set** (ours): decide, before any piece is made, **what is fixed** — the type, the hierarchy, the figures and their
order, the end card's structure — and **what each audience may change** — palette temperature, pace, the hero image,
the allusion (§4). Each piece is then the kit reconfigured, and the set reads as one family whose members differ for a
reason. Write the kit as its own section of `direction.md`, before the audiences' sections: the fixed parts, the
variable parts, and for each variable part the evidence that set it per audience.

---

## 8. The Concept Presentation

> **Implemented by**: `concepts.md` in the project, the workflow's question to the director (or, in an autonomous run,
> its recorded decision), and `Stage.book`, which lays the file out a concept to a page.

Designers organise and explain concepts as a short sequence of pages — text and pictures, simple and sketchy rather than
finished, a header on each naming the project — and Lupton presents it as a tool for **thinking** as much as for
communicating (*Concept Presentations*, pp. 108–109). Her outline comes from BJ Fogg's chapter "Conceptual Designs" in
Brenda Laurel's *Design Research* (MIT Press, 2003): title, overview, challenge, views, solution, benefits, drawbacks. A
slide template made from the same chapter (L. Galli's, `reference/decks/`, ledgered 2026-10-24) adds the sections that
make the file most useful to an agent: **the people's perspective, a storyboard beside the sketch, theoretical
justifications with their sources, early research results, shortcomings stated self-critically, expansion, and next
steps.** Fogg's chapter itself is not in `reference/` and is cited through both. Write it as `concepts.md`, one
top-level heading per concept, before choosing:

```text
# A — <the concept's name>
**Overview.** <one sentence: §2's sentence, as this concept answers it>
**Audience.** <who it is for and what they bring to it, from their measured profile — the people's perspective>
**Views.** <the sketch: the thumbnail or frame already drawn, by path; for a film, the storyboard — its beats, a line
each; the method — verb, figure or sign (§3–§5); the resolve>
**Solution.** <what it does: how it carries the key messages, in order>
**Justification.** <why these choices — the manual sections and research that support them, cited>
**Evidence.** <the early results: what the audience's profile and the first renders actually showed, each with its source
kind — Qloo, research, the brief; and what was assumed>
**Drawbacks.** <its shortcomings, stated self-critically; this is the field that earns the file>
**Expansion.** <what else it could do: other audiences, sizes, a still from the film — what the kit (§7) would keep>

# Recommendation
<the chosen concept and why; why each other lost; the next steps for the chosen one>
```

- **Three concepts, three methods** (ours, after §1): one from a verb (§3), one from a figure (§4), one from a sign
  chosen for its kind (§5). Three variations of one idea are one concept.
- **Test each against the sentence (§2).** The one that carries the key messages most directly, to this audience, wins.
- **Keep it rough.** The views are the thumbnails already made, named by path; nothing new is drawn for the file. The
  director chooses between ideas, not between finishes, and the question to them points at the file.
- **The drawbacks are a watch-list** (ours). The chosen concept's drawbacks are what the checks look for at the end: each
  one is shown to have been avoided, or recorded as a finding. A drawback written and never looked at again is the
  file's one wasted line.
- **A film ends on its resolve.** Where a concept builds to a title, a product or an ask, its last shot is the lock-up
  the earlier shots were close-ups of (Colette & Shaw, ch. 1; Manual 25 §12). Decide the resolve with the concept, not
  after it, and name it in *Views*.

At the end of the run, `Stage.book` makes the file the concepts section of the process book (Manual 18 §3c), each concept
on its own page.

### A concepts sheet, run end to end

One core image — a cab, drawn as three shapes — put through three verbs, beside an antithesis whose two figures are
drawn **by area** with `Scale.radiusFor(...)`, so the figure of speech never changes a quantity (§4). Each cell names
its method, as the sheet shown to the director should.

```javascript
// Concepts sheet: one core image through verbs (§3), and an antithesis drawn by area (§4).
const W = 900, H = 700, canvas = createCanvas(W, H), ctx = canvas.getContext('2d');
ctx.fillStyle = '#f2efe8'; ctx.fillRect(0, 0, W, H);

// The core image: a cab as a body and two wheels, drawn about (x, y) at scale s.
function cab(x, y, s, colour) {
    ctx.save(); ctx.translate(x, y); ctx.scale(s, s);
    ctx.fillStyle = colour; ctx.fillRect(-60, -30, 120, 40);
    ctx.fillRect(-30, -55, 60, 25);
    ctx.fillStyle = '#15151a';
    ctx.beginPath(); ctx.arc(-35, 12, 14, 0, Math.PI * 2); ctx.arc(35, 12, 14, 0, Math.PI * 2); ctx.fill();
    ctx.restore();
}

// One labelled cell per concept: its method, then the image.
function cell(col, row, label) {
    const x = 30 + col * 435, y = 30 + row * 335;
    ctx.strokeStyle = '#15151a'; ctx.lineWidth = 1.5; ctx.strokeRect(x, y, 405, 305);
    ctx.fillStyle = '#15151a'; ctx.font = '20px Georgia'; ctx.fillText(label, x + 14, y + 30);
    return { cx: x + 202, cy: y + 170 };
}

// Magnify: the cab too big for its street.
let c = cell(0, 0, 'Verb: magnify');
ctx.fillStyle = '#9a958a'; ctx.fillRect(c.cx - 190, c.cy + 40, 380, 6);
cab(c.cx, c.cy + 10, 2.2, '#e8b92e');

// Multiply: one cab becomes a fleet.
c = cell(1, 0, 'Verb: multiply');
for (let i = 0; i < 12; i++) cab(c.cx - 150 + (i % 4) * 100, c.cy - 70 + Math.floor(i / 4) * 65, 0.55, '#e8b92e');

// Dissect: the cab's parts pulled apart along their own axes.
c = cell(0, 1, 'Verb: dissect');
ctx.fillStyle = '#e8b92e';
ctx.fillRect(c.cx - 60, c.cy - 10, 120, 40); ctx.fillRect(c.cx - 30, c.cy - 95, 60, 25);
ctx.fillStyle = '#15151a';
ctx.beginPath(); ctx.arc(c.cx - 110, c.cy + 80, 14, 0, Math.PI * 2); ctx.arc(c.cx + 110, c.cy + 80, 14, 0, Math.PI * 2); ctx.fill();

// Antithesis: two true figures side by side, each circle's area proportional to its value.
c = cell(1, 1, 'Figure: antithesis (by area)');
const medallion = 500000, driver = 31000, rMax = 110;
ctx.fillStyle = '#e8b92e';
ctx.beginPath(); ctx.arc(c.cx - 60, c.cy, Scale.radiusFor(medallion, medallion, rMax), 0, Math.PI * 2); ctx.fill();
ctx.beginPath(); ctx.arc(c.cx + 130, c.cy + 82, Scale.radiusFor(driver, medallion, rMax), 0, Math.PI * 2); ctx.fill();
ctx.fillStyle = '#15151a'; ctx.font = '16px Georgia';
ctx.fillText('a medallion', c.cx - 100, c.cy + 128); ctx.fillText('a driver, a year', c.cx + 80, c.cy + 128);
canvas;
```
