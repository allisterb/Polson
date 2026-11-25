## The type: promo

**A narrated motion pitch for whatever the director names** — a product, a service, a studio, a project, an event, a
cause, an idea — made from the brief and the material in `documents/`. Ninety seconds by default. It persuades **one
kind of viewer** to **believe one thing** and **do one thing**, and every claim it makes is the material's, checked
against the page it came from. A good one is what its owner would put on a landing page or play before a talk: short,
exact, produced, and claiming nothing the material does not.

Where this type and the instructions above disagree, this type wins: there is no film to look up and no poster to carry
over unless `documents/` holds one. The subject is the director's. **Audiences are optional.** With none, make one film
for the viewer the brief describes; with `Audience A`, `Audience B`… lines, one film per audience as the instructions'
*More than one audience* says — the case the same in every film, read once from the material, only its telling each
audience's own.

**What this is not.** It is not a startup's case to investors (that is `pitch`, with its projections and the ask's
basis), and not a fifteen-second spot for a consumer product (that is `product`, with its packshot and comparables).
If the brief is one of those, say so in Q1 and propose the other type; the director decides.

### What the brief gives

The subject; who the film persuades; what they should believe and do; the material; the length; narration. Fill the
brief table in `brief.md` with these rows in place of the film ones, and ask for what is missing:

| Parameter | Value |
| :--- | :--- |
| The subject — its name, and what it is in one sentence | |
| Who it persuades — who they are, and what they already know | |
| The one thing they should come away believing, in the brief's words | |
| What they should do next (the ask), and where it sends them — words only; no real URL unless the brief gives one | |
| The material in `documents/` — file, what each is, its date — or none | |
| A logo, a brand guide, product shots or screenshots among them (file names), or none | |
| Length (90 seconds by default) and narration (Enabled, or Disabled) | |
| Anything explicitly ruled out | |

### The spine — before anything is read closely

A promo for *anything* is a sizzle reel for nothing unless three things are fixed first, and every later choice answers
to them:

1. **Who it persuades.** Customers, a funder, a council, a hiring candidate, collaborators, a festival jury — the order
   of the scenes depends on it (*Scenes*, below).
2. **The belief**: the one sentence the viewer should leave with, **in the brief's words**. Not a slogan you write: a
   line from the brief or the material, so the read-back finds it with source `brief` or `document`.
3. **The ask**: what the viewer should do next, in the brief's words. The film ends on it.

**Where the brief leaves one out, propose it in Q1 from the material**, quoted with its page, and ask. Never proceed on
a spine you supplied as though the director had chosen it; if the director says to proceed, record it in
`direction.md` as *assumed, not confirmed*. A brief with all three still gets them read back to the director in Q1, in
one line each: they are the film's contract.

### The order of work — and the board comes before motion

polsonpromo1 built the film first and drew its board from the finished composition afterwards; the board-to-film check
then compared the film with itself and found it 100% alike, and no concepts were ever made. The order is the check:

1. **Sources**: the reading, the spine, the key messages, Stage 1b, Q1.
2. **Concepts**: the brain dump and three concepts in `concepts.md`, one chosen, before any frame is drawn.
3. **Beats**: the scene list as one `SCENES` table (name, start, end, claims, voice line) at the top of `motion.js`, and
   the narration recorded. Every time anywhere — the board's captions, the cuts, `comp.blank`'s `cuts` — is read from
   this table, never typed again, so a scene retimed is retimed everywhere.
4. **The design board**, `03_board.png` and its stills (`ARGS.mode: 'board'`), drawn **before `motion.js` has a single
   key**: each frame a still design of its scene at its key moment, then `Stage.note`d with the time. **It is not redrawn
   from the motion afterwards**: when the motion has to change the design, that is a decision for `direction.md`, and
   the first stills are what Stage 5 compares against. A board drawn from the finished film measures nothing.
5. **Motion**, then **checks**, then **delivery**, as the instructions say. Encode no part before the board exists.

### Stage 1 — the reading: what the material says

**The material is the case.** Find it with `Documents.list()`, then read each document in **one** call with a schema,
**at the top of `motion.js`**: the same question of the same document is served from the cache for nothing, so every
later run asks again rather than copying claims by hand. A PDF or a text file is read page by page, and each figure an
answer uses is looked for on its page by the engine; an image (a screenshot, a product shot) is read as a picture; a
video goes through `Video.ask`.

```javascript
const DOC = 'documents/<the main document>';
const ASK = 'Summarise what this document says about <the subject, as the brief names it>, as material for a short '
    + 'film that persuades <who it persuades>. For each point give the page, one plain sentence of what the document '
    + 'claims, its words quoted exactly, and every figure it prints for that point, exactly as printed, with its unit '
    + 'and the date or period it refers to. Mark each figure\'s kind: "actual" for something already true or done, '
    + '"projection" for a forecast, "target" for a goal, "estimate" for an outside estimate. Mark each point\'s kind: '
    + '"what", "how", "for_whom", "problem", "proof", "why_now", "who", "ask", or "other". List only what the document '
    + 'says: no figure, person, customer, partner or claim it does not contain.';
const POINTS = ['what', 'how', 'for_whom', 'problem', 'proof', 'why_now', 'who', 'ask', 'other'];
const KINDS = ['actual', 'projection', 'target', 'estimate'];
const SHAPE = { type: 'object', required: ['points'], properties: { points: { type: 'array', items: {
    type: 'object', required: ['point', 'page', 'claim', 'quote', 'figures'], properties: {
        point: { type: 'string', enum: POINTS },
        page: { type: 'integer' },
        claim: { type: 'string', description: 'what the document claims here, in one plain sentence' },
        quote: { type: 'string', description: 'the document\'s own sentence, exactly' },
        figures: { type: 'array', items: { type: 'object', required: ['label', 'printed', 'unit', 'period', 'kind'],
            properties: { label: { type: 'string' }, printed: { type: 'string' }, unit: { type: 'string' },
                period: { type: 'string' }, kind: { type: 'string', enum: KINDS } } } } } } } } };

const read = await Documents.ask(DOC, ASK, { schema: SHAPE });
// 'Pending': the read outlived this script and is finishing into the cache. Run the script again, unchanged.
if (!read.success) { error(read.remedy); exit(read.failureName); }
if (read.warnings.length) for (const w of read.warnings) error('SCAN: ' + w);
const points = read.answer.points;

// A figure is usable when every number in it, as printed, is in a quote the engine found on its page.
const numbersIn = s => (String(s).match(/\d[\d,]*(?:\.\d+)?/g) || []).map(n => n.replace(/,/g, ''));
const onPage = new Set(read.evidence.filter(e => e.found).flatMap(e => numbersIn(e.quote)));
for (const p of points) for (const f of p.figures)
    f.found = numbersIn(f.printed).length > 0 && numbersIn(f.printed).every(n => onPage.has(n));
log(`${read.provenance.model} via ${read.provenance.route}; unverified: ${JSON.stringify(read.unverified)}`);
```

- **Each other document in a call of its own**, asking what that document is for: a one-pager the same way; a
  screenshot or product shot *"Describe what this shows: the thing itself, any text on it quoted exactly"*; a brand
  guide for its colours, type and rules, as a poster reads one.
- **A long document**: `Documents.find` the pages first (free), then `ask` with `pages`.
- **A figure not `found` stays off the screen**, and so does a reading of a picture (`read.fromImages`) until the
  director confirms it in Q1. Where `read.provenance.fallback` is set, `read.evidence` is empty and **every figure is
  unchecked**: say so in `direction.md` and in Q1.
- **With no material, the brief is the material.** Every claim is then the brief's, word for word, and the film is
  only as full as the brief; say so in Q1 rather than filling it. Nothing about the subject comes from memory or from
  the web — that includes a subject you happen to know.
- **Record it**: `Stage.check('every claim and figure on screen is the material\'s', …, 'N found, M computed, K from the brief, L left out')`.

### The key messages — the spine, set in words

As the product type does after Lupton (*How to Refine a Creative Brief*, *Graphic Design Thinking*, pp. 56–57), list
the key messages before any design. `direction.md` opens with them:

| Key message | Words as they appear | Where (scene, seconds) | Checked by |
| :--- | :--- | :--- | :--- |
| **What it is** | the descriptor | the *what* scene and the end card | read-back, source `brief` or `document`; the cold viewing |
| The subject's name | as the brief gives it | title and end card | read-back |
| The belief | the brief's or the material's words | the scene that earns it, and the voice | read-back |
| The ask | the brief's words | end card | read-back |

- **The descriptor** is what the subject is in two to five words, **from the brief's own sentence** — *"an AI graphic
  design studio"*, not a category you would put it in. A picture does not say what a thing is; words do. It is set
  beside the subject's first clear showing and on the end card.
- **The cold viewing**, once the film is final: a separate model watches it without the brief, and the question names
  nothing about the subject:

  ```javascript
  const seen = await Video.ask('output.mp4',
    'What is this film about, and what is it asking the viewer to do? Answer in two short sentences.');
  log(seen.success ? seen.text : seen.error);
  ```

  Record the answer word for word in the table, then `Stage.check('a viewer who has not read the brief can say what it
  is and what it asks', <your reading of it>, seen.text)`. If it names another kind of thing or another ask, the film
  has failed its first job: make the descriptor or the ask larger, hold it longer, or bring it earlier, and ask again.
  Never ask a leading question to make it pass. With several audiences, ask about every film. It is a machine
  standing in for a stranger, and `direction.md` says so.

### Stage 1b — every claim confirmed by the material

**A claim's words are a claim too, and the page check sees only digits.** Once the scenes are written and before
anything is designed, put **every claim and figure with the exact words it will carry on screen and in the voice**
back to its document in one call, as the pitch type's label check does:

```javascript
const LINES = SCENES.flatMap(s => s.claims.map(c => ({ shown: c.shown, doc: c.doc })));   // as they will be set and said
const byDoc = {};
for (const l of LINES) (byDoc[l.doc] ??= []).push(l);
for (const [doc, lines] of Object.entries(byDoc)) {
    const confirm = await Documents.ask(doc,
        'For each line, say whether this document supports it with exactly that meaning. Watch the scope: who or what it '
        + 'covers, when, and words such as "only", "all", "first", "every", "fastest". Quote the document\'s sentence, and '
        + 'where the line says more or less than the document, give the document\'s own wording. List the words in the '
        + 'quoted sentence that limit or soften it, exactly as written — "as much as possible", "aims to", "attempts to", '
        + '"can", "most", "designed to", "may" — or none. A line that drops one of them claims more than the document.\n'
        + lines.map((l, i) => `${i + 1}. ${l.shown}`).join('\n'),
        { schema: { type: 'object', required: ['lines'], properties: { lines: { type: 'array', items: { type: 'object',
            required: ['n', 'supported', 'quote', 'docWording', 'qualifiers'], properties: { n: { type: 'integer' },
                supported: { type: 'boolean' }, quote: { type: 'string' }, docWording: { type: 'string' },
                qualifiers: { type: 'array', items: { type: 'string' } } } } } } } });
    if (!confirm.success) { error(confirm.remedy); exit(confirm.failureName); }
    const unsupported = confirm.answer.lines.filter(l => !l.supported);
    Stage.check(`every line from ${doc} is its own claim`, unsupported.length === 0,
        unsupported.map(u => `"${lines[u.n - 1].shown}" -> "${u.docWording}"`).join('; ') || 'all confirmed');

    // Checked here, not by the reader: polsonpromo1's "every fact and figure on screen is checked" came back supported,
    // quoting "verify as much as possible every fact and figure". A qualifier in the quote, missing from the line, widens it.
    const HEDGES = /as much as possible|as far as possible|aims? to|attempts? to|designed to|\bmost\b|\bmay\b|\bmight\b|\busually\b|\btypically\b/gi;
    const dropped = confirm.answer.lines.filter(l => l.supported).flatMap(l => {
        const shown = lines[l.n - 1].shown.toLowerCase();
        const words = [...new Set([...(l.qualifiers || []), ...((l.quote || '').match(HEDGES) || [])]
            .map(w => w.toLowerCase().trim()).filter(Boolean))];
        const missing = words.filter(w => !shown.includes(w));
        return missing.length ? [`"${lines[l.n - 1].shown}" drops "${missing.join('", "')}" (the document: "${l.quote}")`] : [];
    });
    Stage.check(`no line from ${doc} drops its sentence's qualifiers`, dropped.length === 0, dropped.join('; ') || 'none dropped');
}
```

- **An unsupported line is rewritten in the document's wording**, and the whole list asked again: a fix is a new claim.
- **A line that drops a qualifier** either carries it ("checks as much as possible of every figure") or is rewritten
  to claim only what the sentence claims without it. Where the qualifier limits another part of the sentence than the
  line uses, keep the check failing with `{ accepted: '<which part it limits>' }`, and say so in `direction.md`.
- A line from the brief is checked by the read-back, with source `brief`; it is not put to a document.
- **Put the quotes in `direction.md`** beside each claim: the line, the document's sentence, and the page.

### The rules of a promo

- **Claims only from the material.** Every statement about the subject — what it does, for whom, how well, how many,
  since when, with whom — is in the brief or a document, in their words or confirmed by Stage 1b. No "best", "first",
  "only", "leading" or comparison with a named competitor unless the material says it in those words. **Add nothing**:
  no customer, partner, award, review, testimonial, figure or feature the material does not name, however plausible.
  The film argues the case the material makes; it does not make a better one.
- **Every number on screen is printed in the material and found on its page, computed in the script from ones that
  were, or stated in the brief.** Show the working in `direction.md`, and never take the model's arithmetic.
- **A projection, target or estimate looks like one**: drawn open (an outlined bar, a dashed line), and labelled with
  its kind and date in words on screen — *"Target, 2027"*. A viewer must never read a forecast as a result, with the
  sound off, from a single frame.
- **The world around the subject is context, not case.** `Research` may describe the subject's field — how things like
  it usually look and are presented, and each audience's place — to set the register and catch a misfire. It never
  supplies a claim about the subject, and an outside fact (a market size, a trend) goes on screen only if the material
  itself states it.
- **The subject's own assets are used as given.** A logo is placed, not redrawn or recoloured. A screenshot or product
  shot is placed with `Skia.Image.load('documents/<file>')` and `comp.image({ image, tl, br })`, framed rather than cut
  out by hand. **A brand guide in `documents/` is the subject's identity and the film follows it**; with none, the
  palette and type are yours — one family, one accent on a neutral, contrast 4.5 : 1 for text — and never another
  organisation's.
- **Other organisations by name only, as the material names them**: no logo, wordmark, product image or trade dress of
  anyone but the subject.
- **No real person in pictures**: no face, likeness or recognisable silhouette, unless a photograph is supplied in
  `documents/` and placed as given. People named in the material appear in type, by name and role as given.
- **A subject that law or platform rules restrict** — alcohol, gambling, medicines and health claims, financial
  products, political campaigning, anything aimed at children — is a question for the director before any design.
  This type settles nothing about the law.
- **What the film is**: the end card's small line says what it is and who made it only as the brief says; never imply
  an endorsement, a launch or an availability the material does not state.

### Scenes

The material has more than the film can hold. **Choose by what the viewer needs to be persuaded, in an order that
persuades**: the order below is Monroe's motivated sequence (Alan H. Monroe, *Principles and Types of Speech*, 1935) —
attention, need, satisfaction, visualisation, action — set as scenes:

1. **Hook** (3–5 s) — *attention*: the one image or line from the material that makes this viewer look. The subject's
   name may wait; the hook may not.
2. **The problem, or the moment** — *need*: what the viewer lives with now, or what has changed, as the material
   describes it, with its figure where it has one. A subject the material frames as an opportunity rather than a
   problem says that instead; never invent a pain.
3. **What it is** — the subject named, **with the descriptor**, shown as simply as the material shows it.
4. **How it works** — *satisfaction*: the few steps or parts that make it work, as a diagram the viewer reads before
   the words (*input → studio → checked film*). One idea per scene; two scenes if the material needs them.
5. **The proof** — what is already true: results, use, a demonstration, a figure — solid, sourced, never a projection
   dressed as one. Where the material has no proof, there is no proof scene, and Q1 says an audience will look for one.
6. **What changes** — *visualisation*: the viewer's situation with the subject in it, **as the material describes
   it**, not as you imagine it.
7. **The ask** — *action*: what to do next, in the brief's words, held long enough to act on. The film ends on it.
8. **End card** (4 s): name or logo, the descriptor, the ask, and the sources line (the documents used).

**Order by who is persuaded**, deciding once and saying why in `direction.md`: for **customers or users**, as above;
for a **funder or sponsor**, the proof moves before *what changes* and the ask says what the support buys, as the
material states it; for **collaborators or a team**, *what changes* comes early as the vision and the ask is what is
needed from them. This is the studio's rule, not a cited one: mark it so.

**About ten seconds a scene.** Sixty seconds holds about five scenes; ninety about seven; two minutes all of them. **What
to drop**: *what changes*, then the second *how* scene; the hook, *what it is* and the ask always stay. Put the scene
list, with its length per scene and what was dropped, in Q1. **The length is the brief's**: cut it only on a
measurement (encode one scene, time it, multiply), never on an estimate.

**The film is encoded in parts, each one script's work.** A script has 30 seconds, and at 1920 × 1080 a frame takes
about 100 ms to render and encode (polsonpromo1: a 12 s scene took 28.7 s, a second under the limit). So:

1. **Encode the first part, and time it**: `const t0 = Date.now(); comp.saveVideo('artifacts/part_0.mp4', { from, to });
   log(Date.now() - t0)`. Divide 20 seconds by its time per second of film: that is the longest part, with room left
   for the rest of the script. At 1080 it is about 8 seconds; a blurred or grainy scene is shorter.
2. **A part ends on a cut where it can**, and a scene longer than a part is split at a held moment within it (a part
   boundary in a hold does not show).
3. **Name parts by their start**, `part_<seconds>.mp4`, from the start: a part retimed is encoded again under its new
   name rather than renamed, since the host may refuse a rename.
4. The parts silent, then `Motion.joinVideos([...], 'output.mp4', { audio })`.

**The silent preview** of a film over about 45 seconds cannot be encoded at 960 wide in one script; make it at 480
(`Motion.videoToWebp('output.mp4', 'output.webp', { width: 480 })`) and say so in `questions.md`.

### Narration

**A voice, unless the brief says none (`Narration: Disabled`).** The screen carries the claims and figures; the voice
carries the argument between them.

```javascript
const spoken = await Assets.speak('<the scene\'s line>', { voice: 'Charon', path: 'audio/s2-1.wav' });
if (!spoken.success) error(spoken.remedy);
const line = Motion.speech(spoken.text, { audio: spoken.path, start: 1.0 });
if (line.unsaid.length) error('the recording says more than the line: ' + line.unsaid.join(' '));
```

- **One line a call, at most two new lines a script** — a new line takes about 13 seconds to record. Pass which lines to
  record in `ARGS` and run the script until all are cached; a cached line costs nothing.
- **The scene's length is its narration's**, plus the holds: a claim is on screen before it is spoken and stays after.
  About 140 words a minute; ninety seconds is about 200 words. Never cut a line to fit a beat.
- **The voice makes no claim the screen and Stage 1b have not checked**, and says a figure as the screen shows it.
- **The film must make its case with the sound off**: every claim the voice makes is on screen too, in fewer words.
- **Let the voice lead the cut** — a J or an L cut (Manual 25 §12d), not voice and picture changing on one frame.

### Visual style — produced, not plain

**Exact is the floor, not the finish.** Decide the style once, before the beats, and record it with its reasons.

- **Every scene has a picture made of the material's facts**: a diagram of how it works, a figure as a bar or ring, a
  supplied screenshot or shot framed, a generated object where the material names one and supplies no image. A scene
  of words alone is a slide.
- **A generated object** comes from `Assets.cutout`: **describe the object, never the ad**, with no text, logo or brand
  on it, and look at its sheet before using it (`artifacts/00_object_sheet.png`). It stands for something the material
  names; it is never presented as a photograph of the subject. `direction.md` says it was generated.
- **A ground with depth** (`comp.radialGradient`), one slow camera move a scene, eased, **never while a claim is being
  read**; one glow a scene, on its key figure or word.
- **Fill the frame**: a two-column grid, picture one side, claim the other, so no more than about a third of the frame
  is empty at the scene's key moment.
- **Figures arrive whole** — never a counter ticking up, never a character cascade on a number.
- **Cuts and wipes, not dissolves**; no motion blur on text.
- **The ask is the resolve**: design the end card first, and let the scenes lead to it (Manual 25 §12b).
- **Two texts never touch, and every text is title-safe**: place a label from its neighbour's measured box
  (`boundsAt(t, { frame: true })`), and check every scene at its key moment with a `Stage.check` before encoding. A
  check that fails is fixed in the layout, never by loosening the check; when a check and a still disagree, render the
  still and believe it.
- **The design board before motion.** Draw it as the instructions say, a frame per scene, with `treatment.md` and
  `dos.md`, and hold the film to it in Stage 5. `pitch.pdf` is the case as its owner would send it: a cover naming the
  subject, the spine in three lines, the board, a frame of each scene with its source, the ask as the resolve frame,
  and *Thank you.* `process.pdf` carries the reading, the scene list and the Stage 1b confirmations.

### More than one audience

The instructions' *More than one audience* holds, with the rule that keeps a set honest: **the case is shared and only
its telling is the audience's.** The same scenes, claims, figures and ask in every film; a film that drops a claim or
a caveat for its audience makes a different promise to different people. Figures as each locale writes them
(`Locale.number`), every figure in every text from the same call, and **never convert a currency**.

### The read-back — after delivery

**`Verify.film(comp, 'output.mp4', { computed })`** is the instructions' read-back. Every claim and figure on screen
must come back with a source: `brief` for the brief's words (the belief, the ask, the descriptor), `document` with its
page for the material's, `computed` with its working for anything derived. An `unsourced figure` comes off the screen;
a line that comes back as `own words` where the brief's were meant was reworded, so restore it. The check records its
own verdict; do not write one for it.

### Questions, for a promo

- **Q1** (after the reading, before any design): **the spine** — who it persuades, the belief, the ask, each quoted
  from the brief or proposed from the material with its page; the scene list with its length per scene and what was
  dropped; each scene's claims and figures, each marked found, computed, from the brief, from an image, or left out,
  with its Stage 1b confirmation; **the gaps** — no proof, no figure for the problem, an unanswered *why this* — each
  with what a viewer will make of it. If the brief reads as a `pitch` or a `product`, say so here.
- **Q2** the tone, pace and voice, proposed from the brief's words and the material's own register.
- **Q3** and **Q4** as the instructions say: the design board, then the sheet.

### direction.md

**The key messages first**, with the cold viewing's answer. Then **the spine** and who it persuades, then the material
— file, what each is, its date, pages, **who read it** (`provenance.model`, any fallback). Then **one section per
scene**: its claims with their quotes and pages, every computed value with its working, anything left out and why.
Then the shared system — type, palette (the brand guide's, cited, or yours, with reasons), the marks for actual and
projected — and the pace line with its evidence. With several audiences, one section per audience, then *Between the
audiences*, which says first what every film shares: the scenes, the claims, the figures and the ask. The read-back's
result goes last.
