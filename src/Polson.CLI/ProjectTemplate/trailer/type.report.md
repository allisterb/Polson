## The type: report

**A motion-graphics explainer that answers an audience's questions about a long document the director supplied** —
an annual report, a 10-K, an earnings release, a fund factsheet. One question, one scene; every figure on screen
read from the document and **checked against the page it came from**. A good one is what an investor-relations team
or a business desk would publish beside the filing: short, exact, and sourced on screen.

Where this type and the instructions above disagree, this type wins: there is no poster to carry over, no film, and
**no Qloo audience unless the brief gives a place and an age band** (see *Pace* below). The subject is the document.

### What the brief gives

The document, in `documents/` (find it with `Documents.list()`); who is watching, in words; **their questions**,
numbered; the length; anything ruled out. Fill the brief table in `brief.md` with these rows in place of the film
ones, and ask for what is missing:

| Parameter | Value |
| :--- | :--- |
| The document — file, company, form, and the period it covers | |
| Who is watching, in words (what they already know) | |
| The questions, numbered, in the audience's own words | |
| Length (45 s by default; about 10 s a question, plus title and end card) | |
| Anything explicitly ruled out | |
| Sound (none, or what) | |

**At most five questions.** Each needs a scene of eight to twelve seconds to be read; more makes a film no one
finishes, and a 60-minute run. With more, ask the director which five.

### Stage 1 — the reading: every question in one call

`Documents.ask` reads a PDF **as text, page by page**, with Nemotron 3.5 Super VL, and returns **evidence**: the page and
the exact figures each answer used, **each already looked for on its page by the engine**. Ask every question in
one call, with a schema, and **keep the call at the top of `trailer.js`**: the same question of the same document is
served from the cache for nothing, so every later run asks again rather than copying numbers by hand.

```javascript
const REPORT = 'documents/<the file>.pdf';
const ASK = 'For a viewer who <who is watching>, answer each question from this document. For each figure, give it as '
    + 'printed in the document, with its unit as the document states it (e.g. "USD millions") and the period it covers.\n'
    + '1. <question one>\n2. <question two>\n3. <question three>';
const SHAPE = { type: 'object', required: ['answers'], properties: { answers: { type: 'array', items: {
    type: 'object', required: ['n', 'answerable', 'headline', 'figures'], properties: {
        n: { type: 'integer' },
        answerable: { type: 'boolean' },
        headline: { type: 'string', description: 'the answer in one plain sentence' },
        figures: { type: 'array', items: { type: 'object', required: ['label', 'printed', 'unit', 'period'], properties: {
            label: { type: 'string' }, printed: { type: 'string' }, unit: { type: 'string' }, period: { type: 'string' } } } } } } } } };

const read = await Documents.ask(REPORT, ASK, { schema: SHAPE });
// 'Pending': the read outlived this script and is finishing into the cache. Run the script again, unchanged.
if (!read.success) { error(read.remedy); exit(read.failureName); }
if (read.warnings.length) for (const w of read.warnings) error('SCAN: ' + w);
const answers = JSON.parse(read.text).answers;

// A figure is usable when every number in it, as printed, is in a quote the engine found on its page. Each number
// separately: "$40.4 billion (282 million shares)" is two numbers, and gluing their digits makes one on no page.
const numbersIn = s => (String(s).match(/\d[\d,]*(?:\.\d+)?/g) || []).map(n => n.replace(/,/g, ''));
const onPage = new Set(read.evidence.filter(e => e.found).flatMap(e => numbersIn(e.quote)));
// A figure that is a name rather than a number (a place, a segment) is usable when a found quote carries it.
const quoted = read.evidence.filter(e => e.found).map(e => e.quote.toLowerCase());
for (const a of answers) for (const f of a.figures) {
    const nums = numbersIn(f.printed);
    f.found = nums.length > 0 ? nums.every(n => onPage.has(n)) : quoted.some(q => q.includes(String(f.printed).toLowerCase()));
}
log(`${read.provenance.model} via ${read.provenance.route}, ${read.provenance.pages} pages; unverified: ${JSON.stringify(read.unverified)}`);
```

- **The check reads digits, not signs.** "$(21.0) million" and "21.0" match. A figure printed in parentheses or with
  a minus is negative: carry the sign over as printed, and compare it with the page yourself.
- **Find a figure by its question and its place in the answer, never by its label.** Two readings of the same question
  word a label differently ("Data Center, fiscal 2026", then "Data Center revenue, fiscal 2026"), and a lookup by exact
  label breaks the first time the question is re-asked.
- **`read.provenance.model` says who read it.** When it is not Nemotron, `read.provenance.fallback` says why, and
  `read.evidence` is empty: **every figure is then unchecked.** Say so in `direction.md` and to the director in Q1,
  and ask again later if the cause was the service; do not present an unchecked figure as checked.
- **A figure not `found` stays off the screen.** Ask again, narrowly, naming the statement or the page —
  *"In the Consolidated Statements of Cash Flows, net cash provided by operating activities for fiscal 2026, as
  printed"* — one figure a call. Measured on a 93-page 10-K, the text read 10/10; read from page images, the same
  model turned 102,718 into 102,714. That is the error this guards against, and nothing about it looks wrong on a chart.
- **A reading of a picture is not a checked figure.** A PDF's chart, diagram and photo pages go to the model as
  images too (`read.provenance.imagePages`), and what it read from them is in `read.fromImages` — **unchecked even
  when found**: the number may be on the page while the bar, quarter or site it is paired with is wrong. Put each in
  Q1 for the director to confirm, label it in `direction.md` as *read from the chart on p. N*, and never let it
  stand as checked. A map's fine symbols (ring sizes, small legends) are beyond the model: a question that needs them
  gets the honest scene, not a guess.
- **A question the document does not answer gets a scene that says so**, in one plain sentence ("The report does
  not break revenue out by country beyond these four"). An honest gap is a result; a plausible figure is the failure.
- **Record it**: `Stage.check('every figure on screen was found on its page', …, 'N found, M computed, K left out')`.

### The rules of a report

- **Every number on screen is either printed in the document and found on its page, or computed in the script from
  ones that were.** A growth rate, a share, a difference: compute it in `trailer.js` from the found figures, show
  the working in `direction.md`, and **never take the model's arithmetic** — its `headline` is a draft of the
  sentence, not a source for a number.
- **A figure carries its period and its unit**, on screen or in the scene's source line: *"Revenue, fiscal 2026
  (year to 25 Jan 2026): $215.9 billion"*. A company's fiscal year is not the calendar year; say whose year it is.
- **Round once, say how, and never toward a nicer story.** 215,938 (USD millions) is "$215.9 billion"; the growth
  computed from unrounded figures, then rounded. The same figure is rounded the same way in every scene.
- **The company's words stay the company's.** Guidance, outlook and risks are reported as the company's ("the company
  expects…", "the report lists…"), never stated as fact or extended. **No forecast of your own, no recommendation**
  — nothing that says to buy, sell or hold, no price target, no "good investment". This is an explanation of a
  document, not advice, and the end card says so.
- **The company's name in type is fine; its logo and marks are not**, unless supplied in `documents/`, and then placed
  as given. No real person — not the chief executive, not a face.
- **Honest charts** (Manual 13 is in front of you): bars start at zero; two figures compared share one scale; a
  chart's every bar is a found figure; no 3-D, no truncated axis. A single figure needs no chart — set it large.
- **A figure needs a comparison, and the document supplies it.** A number alone says little; beside the prior year
  or as a share of the total it says something. Use the comparisons the filing prints — a 10-K gives three years of
  results, so "how fast did it grow" can be three bars, not one. **Never an outside benchmark, an industry average or a
  scenario of your own**: those are figures the document does not contain, and they cannot be checked against it.
- **The chart follows the question.** Categories compared — bars. Change over time — bars by year, or a line once
  there are more than a few points. Parts of a whole — a stacked bar, or a pie only with fewer than six slices. One
  figure — no chart.
- **Colour means one thing for the whole film.** If the accent marks the answer in scene one, it marks the answer in
  every scene; if green means up, red means down everywhere. **Never colour alone**: an arrow or a sign goes with it,
  for viewers who cannot tell the two apart.
- **Say what moved it, if the document does.** A large change the filing explains — a charge that cut a margin, a
  product transition — gets its reason in the company's words, attributed. Leaving it out makes the figure look
  better or worse than the filing does.

### Scenes

The film is **title → one scene per question → end card**:

1. **Title** (3–4 s): the company, the document, the period — *"NVIDIA · Form 10-K · fiscal 2026"* — and, if the
   answers share one headline, that headline in a line: the summary first, the scenes as its evidence.
2. **Each question** (8–12 s): the question first, in the audience's words, held long enough to read; then the
   answer — the figure arriving large, or bars growing (a `comp.rectangle` with one corner keyed — the three
   growing bars in the SDK reference's *Vector animation* section); then the headline sentence; then the **source line**, small and steady: *"Form 10-K,
   p. 51"*, **at 32 px or more at 1080p** — the read-back's watcher misread 26–30 px muted lines ("2026" as "2025") and
   could not read the page numbers at all. **The source line is a claim too**: name the statement or table as the document titles it ("Revenue by
   End Market", not your question's "revenue by market platform") — the title is in the evidence, or ask for it — and
   never an Item number or section you did not read there. The page is `evidence.page`, the PDF's page; where the printed page number differs, give the printed one
   and say which in `direction.md`.
3. **End card** (4 s): the sources — the document and the pages used — and *"Figures from the company's filing. Not
   investment advice."*

**One scene per encoded part.** A 45-second film will not encode inside one script: encode each scene with
`comp.saveVideo('artifacts/part1.mp4', { from, to })` in a script of its own, then `Motion.joinVideos`, as stage 4
says. Plan the beats so every part boundary falls on a cut.

### Visual style — produced, not plain

Exact is the floor, not the finish. The first report film was correct in every figure and looked like a slide deck: flat
bars on a flat ground. A generated video of the same brief looked like a produced piece — depth, light, a sense of
place — and invented a figure. **Do both: the polish, with every number still the checked one.** Decide the style
once, before the beats, and record it in `direction.md` with its reasons.

- **A ground with depth, not a flat fill.** A `comp.radialGradient` from a lighter centre to a darker edge (a vignette)
  under everything, and one slow move per scene — a push-in or a drift with `comp.camera()`, the ground and the
  chart on separate planes (groups with `z`, `keepSize: true`), eased `halt` at both ends. The move is ambient: small,
  slow, and **never while a figure is being read** — let it settle before the number arrives.
- **Light on the one thing that matters.** The scene's hero figure or bar gets a glow: a copy of it, blurred, under
  the sharp one (a group with `group.blur({ size })`, keyed up as it arrives and down to 0 after, which costs
  nothing). **One glow a scene**, on the answer — glow on everything is glow on nothing, and each blur costs about
  20 ms a frame.
- **Bars that are objects.** `radius` on the corners, a gradient fill (`comp.linearGradient` blended `onto` the bar,
  in a group with it) from the accent to a slightly lighter tint, a thin baseline. The other bars stay muted so
  the answer reads first. A bar still starts at zero and still shares its scale with what it is compared to.
- **Figures arrive whole.** A figure rises, fades or scales into place as the number it is — **never a counter that
  ticks up**, which puts every number between zero and the answer on screen, none of them in the document, and
  the read-back will rightly report them. **No character cascade on a figure either**: landing "$60.9B" a
  character at a time shows "$6", "$60" and "$60.9" on the way, numbers the document never printed. Animate a figure
  as one word (`basedOn: 'words'`) or as a whole.
- **Type as the design.** One family, the figures in a heavy weight and the units smaller and lighter beside them
  (*$215.9* large, *billion* small), the question above in a calmer weight, the source line small and steady. A
  scene's figures share a baseline or a column, so the eye compares them without hunting.
- **Cuts and wipes, not dissolves, between scenes.** A dissolve lays one scene's figures over the next one's — a
  frame where two sets of numbers are on screen at once, readable as neither. A `comp.wipe` or a cut keeps every
  frame one scene. **No motion blur on text**, ever: a blurred figure is an unreadable one.
- **Your palette, not the company's.** One accent and its tints on a dark or a light neutral; contrast still 4.5 : 1
  for text. Do not rebuild the company's brand identity — its signature colour as the whole palette, its typeface,
  its graphic devices — so the film reads as an explanation *of* the filing, not as the company's own
  communication. An accent that nods to the subject is fine; a film mistaken for investor relations is not.
- **Ambient detail stays in the background.** A few slow, low-contrast lines or dots (outlines at a low `amount`)
  can give the ground life; nothing ambient may move faster than the figures, or be bright enough to compete with
  them, and the flash check still applies.
- **Every movement has a job.** A move points at the answer, reveals the next figure or marks a change of scene; one
  that does none of those is noise in a film about numbers. One cohesive set of moves, eases and transitions, used the
  same way in every scene.
- **Budget the frame.** Polish costs per frame: time `comp.render(t)` on the busiest moment of each scene before
  encoding, and keep a scene's part inside its script — one glow and one gradient ground are cheap; five blurs at
  1080p are not.

### Pace

**With no place and age band in the brief, there is no Qloo profile to read**: pace from Manual 25 and the reading
time, and mark it *assumed* in `direction.md`. A figure is read slower than a word — hold a number at least a second
longer than its words would need. **With a place and an age band**, profile the audience for pace and grade
exactly as the instructions above say.

### The read-back — after delivery

The checks above measure the film; this one reads it the way a viewer does. **`Video.ask` watches `output.mp4` and
transcribes every number on screen**, and you compare its list with the figures you set:

```javascript
const seen = await Video.ask('output.mp4',
    'List every number shown on screen, exactly as shown, with the words beside it and the second it first appears.');
if (seen.success) log(seen.text);
```

- **Every number it reads must be one you put there.** One you did not set is a problem in the film — a stray digit,
  a half-drawn figure caught mid-animation. One you set that it missed may simply have been on screen too briefly for
  a watcher sampling twice a second: check that figure's hold on the half-second sheet.
- Record it with `Stage.check(..., { judged: true })` — a model read it, nothing measured it.

### Questions, for a report

- **Q1** (after the reading, before any design): each question with its proposed headline and its figures, each
  marked found, computed, or left out — and any question the document does not answer. The director confirms the
  facts before anything moves.
- **Q2** the tone and pace, with the length per scene.
- **Q3** and **Q4** as the instructions say: the animatic, then the sheet.

### direction.md

The document (file, company, form, period, pages), **who read it** (`provenance.model`, `route`, and any fallback),
then **one section per question**: the question as asked, the headline, every figure with its page and the quote it
was found in, every computed value with its working, and anything left out and why. Then the shared system — type,
palette, the chart style — with its reasons. The read-back's result goes last.
