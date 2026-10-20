## The type: product

**One product, a set of audiences, one print ad each**, each in the visual register of what that audience's buyers
love. This is what advertising calls dynamic creative: one template, a variant per audience. Here you design both —
**the template is the system the set shares** (the product, its name, the claim, the call to action, the type and
grid), and **each variant is what that audience's evidence decides** (image, setting, composition, palette). An ad
that would look the same without the evidence has not done the job.

### What the brief gives

The product's name and what it is; **two or three real comparable brands**; the one claim; the call to action; the
audiences; and, in `documents/`, often a product shot and a logo. Fill the brief table in `brief.md` with these rows
in place of the film ones, and ask for what is missing:

| Parameter | Value |
| :--- | :--- |
| The product — name, and what it is in one sentence | |
| Comparable brands (real, two or three) — and what each resolved to | |
| The claim, worded exactly as it may appear | |
| Call to action (words only; no real URL unless the brief gives one) | |
| Product shot and logo in `documents/` (file names), or none | |
| Audiences A, B, … — place, age band | |
| Where it will be seen, and from how far; page size and bleed | |
| Anything explicitly ruled out | |

### The rules of the product

- **Comparables are signals, never imagery.** No logo, wordmark, packaging, trade dress or slogan of a real brand,
  and no colour or shape that reads as one brand's signature. They tell Qloo what kind of product this is; that is
  all they do.
- **Claims come only from the brief.** No health, nutrition, environmental or performance claim, no "best", "#1" or
  comparison with a named brand, no price or offer, no testimonial, rating, award or certification unless the brief
  or a document gives it in those words. It is the festival-laurel rule of the film type, in a place where an
  invented claim is also against the law.
- **The product's own assets are used as given.** A logo in `documents/` is placed, not redrawn or recoloured. A
  product shot goes in with `paper.image(Skia.Image.load('documents/<file>'), x, y, w, h)` — the object, not the
  path — at 150 ppi or better at print size, which the print file's checks measure. With no shot, make a packshot
  (below); a cutout cell goes straight into `paper.image(cell, x, y, w, h)`.
- **No real person** appears on these ads, as on every poster here.
- **A product that advertising law restricts by age or place** — alcohol, gambling, medicines, financial products —
  is a question for the director before any design. Qloo measures taste; it knows nothing about the law.

### The evidence, measured

Probed on a fictitious cold brew in Berlin, Lagos and Seoul (2026-10-19). Stage 2 for a product is this, in place of
the film's:

1. **Resolve each comparable** (`Taste` op `resolve`, `type: 'brand'`) and **read what it matched**: "Starbucks
   Reserve" resolved to plain Starbucks, a mass-market brand in a specialty set, with no warning. Then
   `demographics` on the ids. **If the brief's age bands sit against the comparables** (an audience of 25–29 against
   brands that skew 35 and over), say so to the director in Q1: one of the two is wrong for this product.
2. **The product's buyers there, less its buyers anywhere.** For each audience, `insights` for `artist` and for
   `brand` with the comparables as the signal and the audience's place and age; once for the whole set, the same
   with no audience. What an audience's list holds that the global one does not is **what is local about this
   product's buyers** — the strongest evidence a product has:

   ```
   Taste({ op: 'insights', type: 'brand', take: 50,
           audience: '{"location":"Lagos","age":"25_to_29"}',
           query: '{"signal.interests.entities":["<id>","<id>","<id>"]}',
           label: 'audience A buyers: brand' })
   Taste({ op: 'insights', type: 'brand', take: 50,
           query: '{"signal.interests.entities":["<id>","<id>","<id>"]}', label: 'buyers anywhere: brand' })
   ```

   Measured: Lagos's buyers shared 3 of 50 brands and 0 of 50 artists with the global list; Berlin's and Seoul's
   shared about 20 brands each — and **shared 14 local brands with each other**. A product's buyers can converge
   across cities. When they do, say so and keep the difference where the evidence has one; do not invent one.
3. **Brand tags are the visual register.** Brands carry design-language tags no film has — "Minimalist Typography",
   "Clean Lines", "Neutral Color Palette", "Heritage-Inspired". **Read them in a script**,
   `Taste.find('audience A buyers: brand').entities`: the tool's reply shows only the first eight tags of each. When
   every audience's brands carry the same ones, they are the **category's** look — cite them as that, for the shared
   system, and not as a difference between audiences.
4. **The setting comes from local places**, `insights` with `type: 'place'`, the comparables as signal, and
   `"filter.location.query": "<the place>"` in the query. **Without that filter the places are in other countries**
   (Lagos's came back as hotels in Houston and Atlanta). Keep the mood tags — Stylish, Romantic, Late night,
   Bustling, Sophisticated — for where and when the product is shown, and its light. Amenities ("Mini fridge",
   "Paid parking") are not evidence.
5. **The place's own brands** — the profile's baseline — are the commercial landscape the ad will hang in. Context
   for what would look foreign or familiar there; never marks to borrow.
6. `signal.location.weight` changed nothing when measured. Do not rely on it.

**Cost**: about ten requests per audience (the profile's seven, two for buyers, one for places) and seven for the set.

### The packshot — after the evidence, before the concepts

The **packshot** is the product as the ad shows it, on nothing. In a spot, the closing shot of the product with its
name and the call to action is the *end packshot* — the end card. **With a product shot in `documents/`**, read it
before designing (`Documents.ask('documents/<file>', 'Describe the product: its shape, its colours, any text on it')`)
and place it as given. **With none**, make one: the model generates the vessel, and you design and set the label.

1. **The label is design, and it comes from the evidence.** The name, its type and two or three colours rest on the
   category's look — the design tags every audience's buyers share (step 3 above) — and on the brief. Decide them
   before generating anything, and record them in `direction.md`.
2. **Generate the vessel as one sheet**, every view in one generation so it is the same object in each:

   ```javascript
   const pack = await Assets.cutout(
     'a slim 250 ml aluminium can, matte black, a wide plain cream band round the middle, no text, no logo',
     { variants: ['front', 'three-quarter', 'top-down'], size: 768, style: 'clean studio product photograph, soft top light' });
   if (!pack.success) { error(pack.remedy); exit(pack.failureName); }
   if (pack.warnings.length) log(pack.warnings.join('\n'));
   ```

   - **Describe the object, never the ad.** A descriptor naming an advertisement, an ad, a billboard or a flyer is
     refused before the network. So is a `style` naming a background: the ground belongs to the call.
   - **No text, no logo, no brand on the generated can.** Generated lettering comes back garbled and is not yours;
     the wordmark is set in type in step 4. Not the shape, colours or band of a real brand's can either — the
     comparables are signals, never imagery.
   - The same descriptor and options are served from the cache for nothing, so a later script asks again rather
     than storing the sheet. One generation from the run's asset budget; a regeneration is another.
3. **Look before using it.** Draw the cells side by side on one canvas and write it as `artifacts/00_packshot_sheet.png`
   — and peek at it. Check that it is the same can in every view, that no lettering crept onto it, that `cell.holes` is near
   zero (a can has no holes), and that `pack.warnings` names no cell. On a failure, change the descriptor once and
   regenerate; a second failure means the product is drawn as shapes instead, and `findings.md` says why.
4. **Set the label in type on the front view.** The plain band is where the name goes: your typeface, centred on
   the band, fitted to its width. On the three-quarter view, either compress the name toward the far edge so it reads
   as wrapping, or leave the band bare and set the name beside the can. Never on the top-down view.
5. **The same packshot in every ad.** It is the template's anchor, held across the set like the end card; the
   variation is everything around it — setting, light, the ground's palette.
6. **`direction.md` says what it is**: generated with `Assets.cutout` (give the sheet's `id`), label designed and
   set by the studio. Never "a photograph of the product", and never anything implying the product is on sale.

### The ad

- **Message** (stage 3) is the product name, the claim as the brief words it, and the call to action. **Q4** for a
  product is which of those a passer-by must take away if they read one thing.
- **Concepts** (stage 4): three per audience, as above, each starting from a different item in *that audience's*
  buyers or places. **Q6 adds: one ad for all, or a series?** A series holds the product, its name, the claim, the
  call to action, the type and the grid, and varies the image, setting, composition and palette.
- **The product reads** from the far render: its name or its shot, whichever the brief leads with.
- **The logo and call to action sit in one place across the set** — the template's anchor.

### direction.md

The shared system first — product, claim, call to action, type, grid — each cited to the brief or a document. Then
**one section per audience**: its three concepts, its buyers (local-only brands and artists, with counts), its
setting (places and mood tags), and every choice with its evidence. Each section after the first opens with what its
ad does differently from those before it, and which of its own items caused each difference.

### The set

As for a film: the finals side by side in `artifacts/set.png`, each labelled with its audience; name the two that came
closest and whether their buyers were close too.
