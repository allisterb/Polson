## What this workflow tests

`math_explainer` is the mathematics route through the animation model: equations from LaTeX (`comp.equation`),
axes and functions (`comp.axes`, `plot`, `area`, `riemann`, `tangent`), numbers that count (`comp.number`), narration
(`Assets.speak`, `Motion.speech`), and the checks that make an explanation sound: `Equation.parse`,
`Verify.derivation`, `Verify.claim` and the read-back. Reach for `polson://sdk/core/Motion`, `polson://sdk/core/Equation`,
`polson://sdk/core/Verify` and `polson://manual/25`.

Answer these in `findings.md`:

- **The checks before the drawing.** How many formulas, steps and claims did the plan carry, and what did each check
  say: proved, agrees, differs, unsettled? Quote every step that **differed** and its counterexample, and what you
  changed. Which steps needed a domain, and is it on screen?
- **The claims.** For each `Verify.claim`: its verdict, and whether its `code` checked what the claim says. Quote any
  check you judged to test the wrong thing, and what you did about it.
- **The read-back.** How many equations and texts did the watcher read as set, miss, or read as something else? Did
  the derivation as read hold? Which equation was missed, and how long was it on screen?
- **Pace and layout.** The shortest hold of any equation, and whether the overlap check found anything. What did the
  sheet show that the plan had not?
- **Narration.** Which voice and why; did any recording say more than its line (`unsaid`), and did the highlight land
  on the words it was timed to?
- **The tools.** What did *Equation*, *Axes* and *Numbers that count* give you, and what did you want from them that
  they did not give? Name any LaTeX the engine would not parse.
- **Where it ran out.** What did the explanation need that the engine could not do, and what did you do instead?
