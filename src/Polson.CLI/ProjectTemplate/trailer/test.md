## What this workflow tests

`trailer` is the motion-graphics route through the animation model: shapes and groups keyed with eases,
**text with animators** (`comp.text`), **blur** (`group.blur`), the camera and its planes, write-ons, wipes,
the grade, cuts, contact sheets as the agent's only view of the film, and delivery as `comp.saveVideo`.
Reach for `polson://sdk/core/Motion` (*Layers*, *Type*, *Nodes*, *Video with sound*) and `polson://manual/25`.

Answer these in `findings.md`:

- **The audience in the motion.** Quote `pace()`'s counts and the titles behind them, and the grade's
  references (result id and item). If you compared audiences, quote the comparison's summary
  (`comparePace`, or `comparePaceAll` for more than two). Did the evidence give the films a reason to
  move differently, and did yours follow it — or did it show no measurable difference, and where did
  you put the difference instead? If the profile had nothing to say
  about a motion choice, which one, and what did you rest it on?
- **More than one audience** (when the brief named several). Did you fetch every profile and compare them
  before planning any film? Was each later film's motion drafted from its own evidence before you looked back
  at the earlier ones? Name what the set shares and what differs, and whether each differing pair's measured
  holds differ in the direction the comparison gave — or why they did not need to.
- **Text animators.** Did the *Type* section give you what you needed to build a cascade? Quote the
  animator you used for the title, and say whether it did what you expected the first time. Name any
  property or selector option you wanted and did not find.
- **Camera, write-on, wipe, grade.** For each you used: what for, whether it did what you expected the
  first time, and what it cost per frame. For each you did not: why not.
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
