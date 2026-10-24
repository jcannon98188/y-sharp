# Spec 14 traceability matrix (TASK-2.14)

Extract of the gating matrix (ADR 0010) for BUD-01..14. Tests and Fixtures columns are counts of tests carrying `[Covers]`; each ID also counts the table-shape test.

Result: 0 active, 14 pending, 0 excluded. Spec 14 added no pending entries and owns no ledger rows. Every entry stays pending with a written reason (`note` in `docs/inventory/budgets.yaml`) because the implementation does not exist yet (ADR 0018, 0019): the helper, the ID table and the tests are in place and skip against the API stub. Pending high-water mark after this spec: registry 16, ledger 0 (unchanged; BUD-01..14 are 14 of the 16).

Counts at hand-off (Release, API stub): the `YSharp.Tests.Allocation` namespace is 661 cases, 22 passing (table, registry cross-check, Yjs baseline, benchmark comparison) and 639 skipped on the stub (hot paths, hostile corpus, slow tier). The slow memory tier (3 bytes-per-item workloads, 1 steady-state check) runs with `YSHARP_TIER=Nightly` in Release.

| ID | Status | Kind | Owner | Declared layers | Covered layers | Tests | Fixtures |
|---|---|---|---|---|---|---|---|
| BUD-01 | pending | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-02 | pending | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-03 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-04 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-05 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-06 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-07 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-08 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-09 | pending | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-10 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-11 | pending | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-12 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-13 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-14 | pending | y-sharp-own | 14 | unit | unit | 2 | 0 |
