# Spec 12 traceability matrix (TASK-2.12)

Extract of the gating matrix (ADR 0010) for CC-1..CC-20. Tests and Fixtures columns are counts. Tier: gating tests are in `YSharp.Tests` (serial "Concurrency" collection); the nightly soak is in `YSharp.Fuzz.Tests` (`YSHARP_TIER=Nightly`).
Seeds: scheduler scenarios run seeds 1..40 plus depth-first enumeration of the tiny ones; stress uses seed 12345; `YSHARP_SEED` pins one. Soak uses random seeds, printed on failure.

Result: 20 active, 0 pending, 0 excluded. Spec 12 added no pending entries and owns no ledger rows. Pending high-water mark after this spec: registry 16, ledger 0.
CC-13 note corrected: Spec 7's oracle probe shows JS has the same rule (a match, not a Divergence).

| ID | Status | Kind | Owner | Declared layers | Covered layers | Tests | Fixtures |
|---|---|---|---|---|---|---|---|
| CC-1 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 13 | 0 |
| CC-2 | active | y-sharp-own | 12 | unit | unit, fuzz | 10 | 0 |
| CC-3 | active | y-sharp-own | 12 | unit | unit, fuzz | 5 | 0 |
| CC-4 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 4 | 0 |
| CC-5 | active | y-sharp-own | 12 | unit | unit | 11 | 0 |
| CC-6 | active | y-sharp-own | 12 | unit | unit | 7 | 0 |
| CC-7 | active | y-sharp-own | 12 | unit | unit | 3 | 0 |
| CC-8 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 9 | 0 |
| CC-9 | active | y-sharp-own | 12 | unit | unit | 6 | 0 |
| CC-10 | active | y-sharp-own | 12 | unit | unit | 2 | 0 |
| CC-11 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 12 | 0 |
| CC-12 | active | y-sharp-own | 12 | unit | unit, fuzz | 5 | 0 |
| CC-13 | active | y-sharp-own | 12 | unit | unit | 4 | 0 |
| CC-14 | active | y-sharp-own | 12 | unit | unit | 2 | 0 |
| CC-15 | active | y-sharp-own | 12 | unit | unit | 4 | 0 |
| CC-16 | active | y-sharp-own | 12 | unit | unit | 5 | 0 |
| CC-17 | active | y-sharp-own | 12 | unit | unit, fuzz | 7 | 0 |
| CC-18 | active | y-sharp-own | 12 | unit | unit, fuzz | 6 | 0 |
| CC-19 | active | y-sharp-own | 12 | unit | unit, fuzz | 6 | 0 |
| CC-20 | active | y-sharp-own | 12 | unit | unit, fuzz | 7 | 0 |
