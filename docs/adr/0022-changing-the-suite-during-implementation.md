# Rules for changing the suite during implementation

Extends ADRs 0003, 0009, 0010, 0011, 0012 and 0020. The suite was written against a stub, so implementing agents will find test expectations that are wrong, divergences nobody recorded, fixtures that need new scenarios and budgets waiting for their first value. Those ADRs already say how each artefact may change. This one says who may make each change, and on what evidence, so the suite cannot be bent to fit the implementation.

## Levels

- **Free.** The agent makes the change and lists it in its notes.
- **With evidence.** The agent makes the change and records the oracle output, or the measurement, that justifies it.
- **Owner.** The agent stops that part of the work and asks (see "Owner decisions").

## Rules

- **A test believed wrong.** The pinned oracle (Yjs v13.6.33) decides, by running the case through fixture-gen or a Node snippet. If the oracle disagrees with the test, fixing the test's expectation or input is *with evidence*. If the oracle agrees with the test, the implementation is wrong. Tests of y-sharp's own decisions are *owner*: `ERR`, `CC`, `BUD`, any `diverge` or `y-sharp-own` entry, and anything an ADR fixes. The two known suspect inputs are fixed *with evidence*, and their expected outcome stays as it is. They are the V2 negative-clock bytes in `Q22_ANegativeClockFromAV2DiffColumn_IsMalformed` and the pending-bytes boundary in `LimitsTests`.
- **An unrecorded divergence.** It is a y-sharp bug by default, and the agent makes y-sharp match (ADR 0003). Recording a new divergence is *owner*: it needs a registry entry, a `[Diverges]` test, a normaliser (ADR 0012) and a line in ADR 0003. The one exception is behaviour that crashes or recurses without bound, which ADR 0003 already says always diverges. That is recorded *with evidence* and reviewed afterwards.
- **The registry.**
  - Free: adding a new active entry with its test for a behaviour discovered during implementation; moving an entry from pending to active once its tests exist; correcting an entry's title, source or layers.
  - Never: adding a pending entry.
  - Owner: retiring an entry, excluding one, or changing its `kind`.
- **Fixtures.** They are never edited by hand. Changing the generator or adding an ops script, then regenerating, is *free*, as long as the regenerated diff touches only the scenarios that changed. A byte change to an existing fixture without a script change cannot happen against a pinned oracle. Investigating it is *owner*.
- **Budgets.** The spec that closes a milestone records its first measured values plus 25% in `AllocationBudgets.Limits` and moves those `BUD` entries to active. That is *with evidence*: the measurement output goes in its notes. Lowering a budget is *free*. Raising a budget or changing a zero budget is *owner*.
- **Weakening the suite.** The following are *owner*: deleting a test, adding `Skip`, changing a feature gate to hide a failure, loosening an assertion (for example from exact bytes to structural), and widening a tolerance or a time budget. The only legitimate skip is a feature that has not landed (ADR 0020). Gating time budgets and `SimCase.GatingSeeds` are calibrated *with evidence* in M7, the first time they are measurable.

## Owner decisions

The agent adds a Backlog comment on its spec headed "Owner decision needed:", with the evidence and a recommendation. It leaves the affected part red or pending and carries on with the rest of the spec. A spec cannot be Done while an owner question on it is open.

## Recording

Every change to tests, the registry, fixtures or budgets goes in its own commit, separate from implementation commits, with a `suite:` subject prefix. Each spec's implementation notes get a "Suite changes" list giving the level and the evidence for each change. This is part of every implementation spec's definition of done.
