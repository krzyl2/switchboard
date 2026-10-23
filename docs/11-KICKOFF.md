# Kickoff prompt

Paste the block below into Claude Code with this repository open. It is written to run as autonomously as possible: it fixes the decisions that are already made, names the few that must stop for a human, and sets the verification bar.

Run it once per stage rather than for the whole v1.0 at once. Stage boundaries exist because each one ends in something installable, and a review there is cheap compared with unwinding four stages of wrong assumptions.

---

```
Build Stage 0 of this project, then stop.

Read first, in this order: CLAUDE.md, docs/01-PRODUCT.md, docs/09-ROADMAP.md,
docs/03-ARCHITECTURE.md, docs/06-DEPLOYMENT.md, docs/10-STACK.md. Read
docs/04-ALGORITHMS.md and docs/07-PITFALLS.md before writing any control logic.
Treat CLAUDE.md as binding on every file you touch.

Target: .NET 10 (net10.0). Decided, do not revisit.

WORK AUTONOMOUSLY
Do not ask me to confirm decisions the documentation already makes, and do not
ask permission to start. Choose freely: project and folder layout, namespaces,
test framework, logging, DI wiring, file names, how to split classes, commit
granularity. Where the docs are silent on something small, pick the obvious
option, write one line about it in docs/DECISIONS.md with the date, and carry on.

STOP AND ASK only when:
- a requirement in docs/02-REQUIREMENTS.md contradicts another one, or
  contradicts the mockups;
- something cannot be verified without my hardware or my Home Assistant
  instance;
- a choice is expensive to reverse and the docs genuinely do not decide it
  (a dependency that shapes the architecture, a persisted data format, the
  MQTT unique_id scheme);
- you would have to weaken a hard rule in CLAUDE.md to make something work.
Collect these and present them together at the end rather than interrupting
for each one.

NEVER, under any circumstance:
- command a Home Assistant entity, or write anything that could, outside an
  explicit test double;
- default the add-on to active mode;
- put a control parameter in code as a literal;
- consume a derived/template sensor as a control input;
- read the Recorder database directly;
- expose a network port other than ingress.

VERIFY AS YOU GO
Every stage ends green: build clean, tests passing, no TODO left in a path
that runs. Control logic is tested on an injected virtual clock, never on
wall-clock sleeps. Write the test before the behaviour where the behaviour is
a rule from docs/04-ALGORITHMS.md. If you cannot test something without my
instance, write the test as far as it goes, mark it explicitly, and list it in
the final report.

WHEN DONE
Report in this order: what runs now, how I verify it myself in under five
minutes, which requirement IDs from docs/02-REQUIREMENTS.md are now satisfied
(tick them in that file), what you decided on your own that I might disagree
with, and what is blocked on me. Do not tick a requirement you could not test.

Then stop. Do not begin the next stage.
```

---

## Running the later stages

Replace the first line with the stage you want and keep everything else. Stage 2 and 3 are the ones worth splitting further if context runs short: Stage 2 into (breaker model + budget) and (shedding + EffectorGate), Stage 3 into (device types + allocation) and (settling + verification + deferred intent).

For Stage 4 and 5 add:

```
The mockup at docs/mockups/index.html is the reference for structure,
vocabulary and information hierarchy. Port its structure; do not paste its
markup. Read docs/05-UI-SPEC.md for what each screen must show and which
requirement IDs it covers.
```

## Parallelism

Only Stage 4 splits cleanly across agents, by screen, once the runtime API exists. Stages 0 to 3 are one sequential thread — the state machine is small, highly coupled, and merging two agents' versions of a lockout rule costs more than writing it once.

## Review checkpoints that matter

Between stages, check these yourself rather than trusting the report:

1. **After Stage 1** — point it at your real meter in read-only mode and leave it a day. Phase values and ages must look right at every hour, including when production exceeds load.
2. **After Stage 2** — drive the fake meter through every tier of the table and confirm the decision log says exactly what you expect, including the recovery of the overload budget.
3. **After Stage 3** — one week in shadow mode on your own installation. Read the whole decision log. The product ships when nothing in it surprises you.
