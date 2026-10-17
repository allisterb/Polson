## What this workflow tests

`trailer` is the motion-graphics route through the animation model: shapes and groups keyed with eases,
**text with animators** (`comp.text`), **blur** (`group.blur`), cuts and wipes, contact sheets as the
agent's only view of the film, and delivery as `comp.saveVideo` and `comp.saveSif`. Reach for
`polson://sdk/core/Motion` (*Layers*, *Type*, *Nodes*, *Video with sound*) and `polson://manual/25`.

Answer these in `findings.md`:

- **Text animators.** Did the *Type* section give you what you needed to build a cascade? Quote the
  animator you used for the title, and say whether it did what you expected the first time. Name any
  property or selector option you wanted and did not find.
- **Blur.** Did you use it, for what, and was its cost on the render time noticeable?
- **The sheets as your eyes.** Were the animatic and the half-second sheet enough to judge the motion?
  Name anything about the film you could not judge from them.
- **The checks.** Which failed, what did you change, and was the change in the keys, the eases or the
  beats?
- **Delivery.** Did `comp.saveVideo` finish inside the script's time limit? How long did it take, and
  were its `warnings` empty? Is `trailer.js` runnable on its own — which mode would an export use, and
  does anything on that path need the session?
- **From the poster.** What carried over unchanged, what did the motion force you to change, and why?
- **Where it ran out.** What did the brief need that the model could not give, and what did you do instead?
