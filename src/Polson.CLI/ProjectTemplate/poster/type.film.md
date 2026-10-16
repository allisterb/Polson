## The type: film

**One film, two audiences, two posters**, each in the visual register of what its audience loves. Same
title, same film; different taste, visibly different posters, each with its evidence. That pair is the
argument this type exists to make.

**The film is fictional, and its comparables are real.** A poster for a real film is that studio's
trademark and key art; a fictional film keeps the work clear of it. The two or three comparable titles
in the brief are real, and they are how Qloo knows what kind of film this is. They are **signals, never
imagery**: do not quote their key art, borrow their title treatments, or draw their actors. No real
person appears on these posters.

### The stages, for a film

These run inside the nine above.

1. **The film** (in stage 1). `Taste` op `resolve` each comparable (`type: 'movie'`), choosing by year
   and disambiguation; ask if two candidates would change the result. What kind of film is this —
   genre, tone, era — in your own words from the logline and the comparables. `describe` on the
   comparables may suggest vocabulary; it is weak evidence, so treat what it returns as prompts.
2. **The audiences** (stage 2). A `profile` for each audience in the brief. Then:
   - **Who the comparables appeal to**: `demographics` on their ids gives each one's skew by age band and
     gender. It is not place-specific. **Do not try to rank them with `filter.results.entities` under an
     audience's signals**: that filter only keeps entities already in the audience's results, so
     comparables outside them come back empty (measured on the first run of this workflow). If a
     comparable *does* appear in a profile's lists, that is the stronger evidence. Say so.
   - **`compare`** the two audiences' distinctive references (`entities` against `versus`) when you
     want to say what separates them rather than only what each holds.
3. **Message** (stage 3) is the title, the tagline if the brief gives one, and the billing. **Q5** for a
   film is the register: the same film can be sold as a thriller or as a love story, and the evidence
   may pull the two posters different ways. Ask before you let it.
4. **Concepts** (stage 4). **Q6 adds: one poster for both, or a series?** A series holds the system —
   grid, type, title treatment — and varies image and palette; each poster must still work alone.
   Film posters may *overwhelm the eye* or *assault the surface* where the audience's references do;
   say which references license it. **Scale as narrative** is the film poster's own convention: a large
   head as the point-of-view character, small figures for what it does (Lupton, *How Posters Work*,
   pp. 140–142). **One frame, one moment**: the peak of the action or its aftermath implies the whole
   story (pp. 160–169).
5. **Direction** (stage 5): **two `direction.md` sections**, one per audience, each with its own
   evidence. Shared choices — the title, the system — are stated once and cited to the brief.
6. **Comps and checks** (stages 6–7). Beyond the checks above: **the title reads** from the far render;
   **the genre reads** — someone who knows none of the references should still guess what kind of film
   it is; **each poster's choices trace to its own audience's evidence**, not to the other's.
7. **The pair** (stage 8). Put the two finals side by side in one render (`artifacts/pair.png`) for the
   run page. If a stranger could not tell they were for different people, the evidence did not reach
   the design — say so in `findings.md` rather than forcing a difference the evidence does not support.

### The billing block

The credits at the foot of a film poster are set small by convention; they are read up close, never
from across the lobby. They are exempt from the distance check and not from the near one, the safe
margin, or contrast. Keep them on one system — one condensed face, one size, tracked consistently —
and keep them **fictional**: invented names, invented companies, nothing that reads as a real studio.
