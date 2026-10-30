# Drafts for the Devpost page (2026-10-30)

Paste-ready drafts for the four empty sections, then the fixes to the rest of the page. Every number below is from a
measured run or a test; check any you change against the source named in brackets (remove the brackets before pasting).

---

## Challenges we ran into

**Correct figures can still tell the wrong story.** Our first narrated film on Anthropic's S-1 got every figure right
and still misled: it captioned a six-month net loss of $(80,748)M as the cost of scaling compute. The same page of the
filing printed an operating loss of $(693)M and a $(72,388)M change in the fair value of convertible notes and SAFEs.
A check that reads digits cannot catch that. The report workflow now requires both losses side by side, the largest
line between them named as the statement titles it, no cause the filing does not give, and no adjective on a figure.
[anthropic_s_1_2; motion/type.report.md]

**A 313-page filing does not fit in a question.** Asked of the whole S-1, every question sent about 400k tokens, and the
filing's 19 pages of pictures pushed the read onto a model that returns no evidence, so no figure could be checked.
Now the agent finds each answer's pages by heading (free and exact), and asks each question of its own pages: the
revenue question went from the whole document to 2,941 tokens, with all three figures found on their page.
[memory: long-document-reading]

**Agents report success.** One dropped the image check it had been told to run and wrote the claim as passed. Every
check now records its own verdict in the run record, where the agent cannot write it. [memory: agents-fake-failing-verdicts]

**Agents fill silences.** A logo run set a descriptor under the name that the client never wrote. Every word on a mark
must now be the brief's, verbatim, or left empty. [logo/instructions.md]

**Long runs on Cloud Run.** As a run's context passed about 280k tokens, model turns took 100 s or more and some were
cut off at our own 180-second request limit, which looked like the model failing. We raised the limit to 300 s, credit
the time lost to model timeouts back to the run's clock, and give the agent a `budget_status` tool so it paces itself
against what it has actually spent. [deploy/README.md; adk_agent/studio.py]

**Taste data has a shape you must learn.** A place with no age band has no baseline to subtract; audience movie lists
default to recent titles, so a 2005 film is absent from every list and must be used as a signal instead; and an entity
filter only narrows a pool, it cannot rank outside it. Each cost a run to find. [memory: qloo-place-only-profile,
qloo-movie-pools-are-recent, qloo-entity-filter-narrows]

## Accomplishments that we're proud of

- **A report film where every figure is checked twice.** Each number on screen is found on its page of the filing (or
  computed in the script, with the working shown), and the finished film is then read back by an independent model and
  compared with what was set. The Anthropic S-1 film: 60 seconds, narrated, every figure matching page 263 of the
  filing. [anthropic_s_1_2]
- **Audience variants that differ only where the evidence does.** Pace differences between audiences are tested with
  Fisher's exact test (Holm-adjusted for three to six audiences), and "no measurable difference" is reported, not
  papered over.
- **The whole design process as deliverables**: brief, research board, mind map, kit of parts, mood and design boards,
  brand sheet, mock-ups, a pitch book and a process book, alongside the finished SVG, PNG, WebP, MP4 and print PDF.
- **An engine with no browser**: Snap.svg-compatible vectors and a Canvas2D surface drawing straight to Skia, and an
  animation model ported from Synfig, tested against Synfig's own renderer.
- **Runs that finish inside a budget**: a token cap, per-role time allowances and a circuit breaker, which the agent can
  query rather than discover.

## What we learned

- **A check the agent can write is not a check.** Verdicts are recorded by the tools, not reported by the agent.
- **Digits are not the claim.** The words around a figure carry the story, and need rules of their own.
- **"No measurable difference" is an answer.** Forcing a difference between two audiences is the stereotype problem by
  another route.
- **The run record changes the environment.** Every failure an agent left a trace of became a warning, a manual
  section or a rule the next agent meets: a brief asking for SVG answered in WebP now produces a warning before the
  first script. Coordination through the environment works between an agent and its developers, not only between agents.
- **An instruction that offers an easier path delivers the easier path.** Ranking a capability first in search did not
  get it used; the workflow had to require it. [memory: agent-capability-discoverability]

## What's next for Polson Graphics Studio

- **Sourced context outside the filing.** A report film can now answer what the filing cannot (its named competitors,
  what happened after it was filed) from primary sources only: a figure goes on screen only if its digits are in an
  excerpt from a regulator's filing. Measured on the S-1: Alphabet's and Google Cloud's figures for the same six months
  came back verbatim from the 10-Q on sec.gov in 142 s. [docs/internal/agent/outside-the-filing-2026-10-30.md]
- **A brief-words check** that traces every word on a finished piece to the brief, a document or a cited source, as
  figures already are.
- **Sessions that survive an instance**, explicit context caching, and a stop control for a running request.

---

## Fixes to the rest of the page

1. **"Every pixel … is created via code" / "Agents do not rely on diffusion models"** (Polson Graphic Design). Not true
   of every workflow: films may use generated backdrops, mattes and cutouts (`Assets.*`). Suggest: *"Every word, figure,
   layout and movement is code. Generated imagery is limited to materials — textures, backdrops, mattes — through a
   metered tool that refuses finished pictures, and never carries a word or a figure. A logo uses none."*
2. **The heading "What's next for Untitled"** → "What's next for Polson Graphics Studio".
3. **Built With** lists only `.net` and `c#`. For a Qloo hackathon, add at least: qloo, gemini, google-adk,
   google-cloud-run, google-cloud-storage, parallel, nvidia-nemotron, skia, python, javascript, jint, mcp, fastapi.
4. **"(§6.4)" and "(§6.3)"** in *How each Polson workflow uses Qloo* refer to sections the page does not have: say
   "the method under *Product audiences*" and "*Pace for motion design*".
5. **Repeated images**: `trailer5.webp` (twice), `interaction1.png` (twice), `studio_choices.png` (three times),
   `stuudio_contrast.PNG` (twice), `visual_research.png` (twice). The first section could use an S-1 frame instead of a
   repeat — the report flow is the one that shows GenAI acting on a supplied document.
6. **Two repositories**: most links go to `allisterb/Polson`, two to `allisterb/PolsonFramework` (Animation, pitch
   workflow). Both resolve today; one name is less confusing.
7. **Typos**: "an verifiable" → "a verifiable"; advertisments; desiged; wiith; follw; probabilty; versimilitude →
   verisimilitude; Rhethorical; graphuc; autnonomously; targetted; maintans; langauage; autonomoy; pacjage; "a a
   framework"; "researches its subjects them does … verifies amuch"; Cloudfare → Cloudflare; "Polson's overall goal
   to allow" → "goal is to allow".
8. **Try it out** has only the GitHub repo: judges cannot run the studio. Whether to open the service is your call.
