# Release traceability matrix (TASK-4.44)

The gating matrix (ADR 0010) at the release gate, written by `TraceabilityGateTests` with `YSHARP_RELEASE_GATE=1` (Release) and copied here unchanged below this paragraph. Every feature is in `Features.Landed` and no test skips for a missing feature; zero registry entries and zero ledger rows are pending; every `BUD-*` entry is active with its recorded value (`docs/inventory/budgets.yaml`). The remaining skips are tier or configuration gates (Release-only allocation tests, Nightly and Smoke tiers, the debug-build lock-order checker), each of which runs in another job.

- Registry entries: 591 (587 active, 0 pending, 4 excluded, 0 retired)
- Pending ledger rows: 0
- Landed features: Encoding, Values, Json, Store, Transactions, Integrate, V1, Gc, UpdateFunctions, V2, V2Functions, TypesCore, Array, Map, Text, TextFormat, Xml, Events, DocLifecycle, Snapshots, Positions, Undo, Subdocs, UserData, Awareness, Sync, Sim, Concurrency, Fuzz
- Violations: 0

## By owner

| Spec | Entries | Active | Pending |
|---|---|---|---|
| 2 | 5 | 5 | 0 |
| 5 | 108 | 104 | 0 |
| 6 | 87 | 87 | 0 |
| 7 | 141 | 141 | 0 |
| 8 | 81 | 81 | 0 |
| 9 | 86 | 86 | 0 |
| 10 | 7 | 7 | 0 |
| 11 | 31 | 31 | 0 |
| 12 | 20 | 20 | 0 |
| 13 | 11 | 11 | 0 |
| 14 | 14 | 14 | 0 |

## Entries

| ID | Status | Kind | Owner | Declared layers | Covered layers | Tests | Fixtures |
|---|---|---|---|---|---|---|---|
| A-1 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 1 |
| A-10 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| A-11 | active | match | 7 | unit, differential | unit | 1 | 0 |
| A-12 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| A-13 | active | match | 7 | unit, differential | unit, port, fixture | 5 | 1 |
| A-14 | active | match | 7 | unit, port, differential | unit, port | 5 | 0 |
| A-15 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| A-16 | active | match | 7 | unit, differential | unit, fixture | 1 | 3 |
| A-17 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| A-18 | active | match | 7 | unit, differential | unit, port, fixture | 4 | 3 |
| A-19 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 1 |
| A-2 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| A-20 | active | unsafe-excluded | 7 | unit | unit | 1 | 0 |
| A-3 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| A-4 | active | match | 7 | unit, differential | unit, fixture, differential | 2 | 2 |
| A-5 | active | match | 7 | unit, differential | unit, port, fixture, differential | 3 | 1 |
| A-6 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| A-7 | active | diverge | 7 | unit, differential | unit | 1 | 0 |
| A-8 | active | match | 7 | unit, port, differential | unit, port | 1 | 0 |
| A-9 | active | match | 7 | unit, differential | unit, fixture | 2 | 1 |
| A-E1 | active | match | 7 | unit, port, differential | unit, port, fixture | 3 | 1 |
| A-E10 | active | match | 7 | unit, differential | unit | 1 | 0 |
| A-E2 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 1 |
| A-E3 | active | match | 7 | unit, differential | unit, port | 1 | 0 |
| A-E4 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 1 |
| A-E5 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| A-E6 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 2 |
| A-E7 | active | match | 7 | unit, port, differential | unit, port | 1 | 0 |
| A-E8 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 1 |
| A-E9 | active | match | 7 | unit, differential | unit | 1 | 0 |
| AU-1 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| AU-2 | active | match | 9 | unit, fixture, differential | unit, fixture | 2 | 1 |
| AU-3 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AU-4 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AU-5 | active | diverge | 9 | unit, differential | unit | 1 | 0 |
| AU-6 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AU-7 | active | match | 9 | unit, differential | unit | 2 | 0 |
| AW-1 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AW-10 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-11 | active | diverge | 9 | unit, differential | unit, fixture | 1 | 1 |
| AW-12 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 1 |
| AW-13 | active | diverge | 9 | unit, differential | unit | 1 | 0 |
| AW-14 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 2 |
| AW-15 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 2 |
| AW-16 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 1 |
| AW-17 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 3 |
| AW-18 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| AW-19 | active | match | 9 | unit, differential | unit, fixture | 1 | 2 |
| AW-2 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AW-20 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AW-21 | active | diverge | 9 | unit, differential | unit | 1 | 0 |
| AW-22 | active | match | 9 | unit, differential | unit, fixture, differential | 4 | 1 |
| AW-23 | active | match | 9 | unit, differential | unit | 1 | 0 |
| AW-24 | active | diverge | 9 | unit, differential | unit | 1 | 0 |
| AW-25 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 1 |
| AW-26 | active | match | 9 | unit, differential | unit, fixture | 1 | 4 |
| AW-27 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 1 |
| AW-28 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-29 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| AW-3 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| AW-30 | active | match | 9 | unit, fixture, differential | unit, port, fixture, differential | 4 | 2 |
| AW-31 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 2 | 1 |
| AW-32 | active | diverge | 9 | unit, fixture, differential, fuzz | unit, fixture, differential | 2 | 1 |
| AW-33 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-34 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 3 | 2 |
| AW-35 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 2 | 1 |
| AW-36 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 4 | 1 |
| AW-37 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-38 | active | diverge | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-39 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 2 | 2 |
| AW-4 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| AW-40 | active | match | 9 | unit, fixture, differential | unit, fixture | 1 | 1 |
| AW-41 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 2 |
| AW-42 | active | match | 9 | unit, differential | unit, port, fixture, differential | 4 | 2 |
| AW-43 | active | match | 9 | unit, differential | unit, port, fixture | 2 | 2 |
| AW-44 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-45 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-46 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 2 |
| AW-47 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-48 | active | match | 9 | unit, differential | unit, port, fixture, differential | 3 | 2 |
| AW-49 | active | match | 9 | unit, differential | unit, fixture | 2 | 2 |
| AW-5 | active | match | 9 | unit, fixture, differential | unit, fixture | 1 | 1 |
| AW-50 | active | match | 9 | unit, differential | unit, port, fixture, differential | 4 | 3 |
| AW-51 | active | diverge | 9 | unit, fixture, differential, fuzz | unit, fixture, differential | 4 | 1 |
| AW-52 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| AW-6 | active | match | 9 | unit, differential | unit, port, fixture, differential | 3 | 2 |
| AW-7 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| AW-8 | active | match | 9 | unit, differential | unit, port, fixture, differential | 4 | 2 |
| AW-9 | active | match | 9 | unit, differential | unit, port, fixture, differential | 6 | 4 |
| BUD-01 | active | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-02 | active | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-03 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-04 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-05 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-06 | active | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-07 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-08 | active | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-09 | active | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-10 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-11 | active | y-sharp-own | 14 | unit | unit | 3 | 0 |
| BUD-12 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-13 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| BUD-14 | active | y-sharp-own | 14 | unit | unit | 2 | 0 |
| CC-1 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 13 | 0 |
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
| CC-2 | active | y-sharp-own | 12 | unit | unit, fuzz | 14 | 0 |
| CC-20 | active | y-sharp-own | 12 | unit | unit, fuzz | 7 | 0 |
| CC-3 | active | y-sharp-own | 12 | unit | unit, fuzz | 8 | 0 |
| CC-4 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 5 | 0 |
| CC-5 | active | y-sharp-own | 12 | unit | unit | 11 | 0 |
| CC-6 | active | y-sharp-own | 12 | unit | unit | 10 | 0 |
| CC-7 | active | y-sharp-own | 12 | unit | unit | 3 | 0 |
| CC-8 | active | y-sharp-own | 12 | unit, fuzz | unit, fuzz | 12 | 0 |
| CC-9 | active | y-sharp-own | 12 | unit | unit | 11 | 0 |
| D-1 | active | match | 7 | unit, differential | unit, port, fixture | 5 | 3 |
| D-2 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 1 |
| D-3 | active | match | 7 | unit, port, differential | unit, port | 2 | 0 |
| D-4 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| DIFF-01 | active | y-sharp-own | 13 | differential | differential | 30 | 0 |
| DIFF-02 | active | y-sharp-own | 13 | differential, fixture | fixture, differential | 8 | 1 |
| DIFF-03 | active | y-sharp-own | 13 | differential | differential | 10 | 0 |
| DIFF-04 | active | y-sharp-own | 13 | differential | differential | 6 | 0 |
| DIFF-05 | active | y-sharp-own | 13 | differential | differential | 21 | 0 |
| DIFF-06 | active | y-sharp-own | 13 | differential | differential | 12 | 0 |
| DIFF-07 | active | y-sharp-own | 13 | differential | differential | 12 | 0 |
| DIFF-08 | active | y-sharp-own | 13 | differential | differential | 7 | 0 |
| DIFF-09 | active | y-sharp-own | 13 | differential | differential | 9 | 0 |
| DIFF-10 | active | y-sharp-own | 13 | differential | differential | 6 | 0 |
| DIFF-11 | active | y-sharp-own | 13 | differential | differential | 4 | 0 |
| DOC-01 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| DOC-02 | active | match | 8 | unit, differential | unit | 1 | 0 |
| DOC-03 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| DOC-04 | active | match | 8 | unit, differential | unit, fixture | 2 | 2 |
| DOC-05 | active | match | 8 | unit, differential | unit | 1 | 0 |
| DOC-06 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| DOC-07 | active | match | 8 | unit, port, differential | unit, port | 2 | 0 |
| DOC-08 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| DOC-09 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 1 |
| DOC-10 | active | match | 8 | unit, differential | unit, fixture | 5 | 1 |
| DOC-11 | active | match | 8 | unit, port, differential | unit, port | 2 | 0 |
| DOC-12 | active | match | 8 | unit, port, differential | unit, port, fixture | 4 | 3 |
| DOC-13 | active | match | 8 | unit, port, differential | unit, port | 2 | 0 |
| DOC-14 | active | match | 8 | unit, port, differential | unit, port | 2 | 0 |
| DOC-15 | active | match | 8 | unit, differential | unit, fixture | 4 | 1 |
| DOC-16 | active | match | 8 | unit, differential | unit, port, fixture | 3 | 2 |
| DOC-17 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| DOC-18 | active | match | 8 | unit, differential | unit | 2 | 0 |
| E-1 | active | match | 7 | unit, differential | unit, fixture | 3 | 3 |
| E-2 | active | match | 7 | unit, differential | unit, fixture | 1 | 2 |
| E-3 | active | match | 7 | unit, port, differential | unit, port, fixture | 3 | 1 |
| E-4 | active | match | 7 | unit, port, differential | unit, port, fixture | 3 | 1 |
| E-5 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| E-6 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 2 |
| ENC-01 | active | match | 5 | unit, fixture, differential | unit, fixture | 6 | 1 |
| ENC-02 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 8 | 1 |
| ENC-03 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 6 | 1 |
| ENC-04 | active | match | 5 | unit, fixture, differential | unit, fixture | 4 | 1 |
| ENC-05 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 8 | 1 |
| ENC-06 | active | match | 5 | unit, fixture, differential | unit, fixture | 7 | 1 |
| ENC-07 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 19 | 2 |
| ENC-08 | active | match | 5 | unit, fixture, differential | unit, fixture | 5 | 1 |
| ENC-09 | active | match | 5 | unit, fixture, differential, fuzz | unit, port, fixture | 8 | 1 |
| ENC-10 | active | match | 5 | unit, fixture, differential | unit, fixture | 7 | 1 |
| ENC-11 | active | match | 5 | unit, fixture, differential, fuzz | unit, port, fixture | 9 | 1 |
| ENC-12 | active | match | 5 | unit, fixture, differential | unit, port, fixture | 7 | 1 |
| ENC-13 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 4 | 1 |
| ENC-14 | active | match | 5 | unit, fixture, port, differential | unit, port, fixture | 4 | 1 |
| ENC-15 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| ENC-16 | active | match | 5 | unit, fixture, differential | unit, fixture | 6 | 1 |
| ENC-17 | active | match | 5 | unit, fixture, differential | unit, fixture | 4 | 1 |
| ENC-18 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-19 | active | match | 5 | unit, fixture, differential | unit, fixture | 4 | 1 |
| ENC-20 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-21 | active | match | 5 | unit, fixture, differential, fuzz | unit, port, fixture, differential | 13 | 3 |
| ENC-22 | active | match | 5 | unit, fixture, differential, fuzz | unit, port, fixture | 6 | 1 |
| ENC-23 | active | match | 5 | unit, fixture, differential, fuzz | unit, port, fixture, differential | 14 | 2 |
| ENC-24 | active | match | 5 | unit, fixture, differential, fuzz | unit, port, fixture, differential | 12 | 2 |
| ENC-25 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 5 | 2 |
| ENC-26 | excluded | match | 5 | unit, fixture, port, differential |  | 0 | 0 |
| ENC-27 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 5 | 1 |
| ENC-28 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 5 | 2 |
| ENC-29 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-30 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-31 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-32 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| ENC-33 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-34 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| ENC-35 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-36 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| ENC-37 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture | 4 | 1 |
| ENC-38 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 4 | 1 |
| ENC-39 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 4 | 1 |
| ENC-40 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-41 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-42 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-43 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-44 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-45 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-46 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-47 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-48 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-49 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| ENC-50 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-51 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 4 | 1 |
| ENC-52 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| ENC-53 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-54 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 2 |
| ENC-55 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-56 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 2 |
| ENC-57 | excluded | match | 5 | unit, fixture, differential, fuzz |  | 0 | 0 |
| ENC-58 | excluded | match | 5 | unit, fixture, differential, fuzz |  | 0 | 0 |
| ENC-59 | excluded | match | 5 | unit, fixture, differential, fuzz |  | 0 | 0 |
| ENC-60 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 1 |
| ENC-61 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-62 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 1 |
| ENC-63 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-64 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 5 | 1 |
| ENC-65 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 1 |
| ENC-66 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-67 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 1 |
| ENC-68 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 1 |
| ENC-69 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| ENC-70 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-71 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| ENC-72 | active | match | 5 | unit, fixture, differential | unit, fixture | 6 | 1 |
| ENC-73 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| ENC-74 | active | match | 5 | unit, fixture, differential | unit, fixture, differential | 3 | 1 |
| ENC-75 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-76 | active | match | 5 | unit, fixture, differential | unit, port, fixture | 3 | 1 |
| ENC-77 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-78 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-79 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-80 | active | match | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| ENC-81 | active | match | 5 | unit, fixture, differential | unit, port, fixture | 7 | 2 |
| ENC-82 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| ENC-83 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| ENC-84 | active | match | 5 | unit, fixture, differential | unit, port, fixture | 4 | 1 |
| ENC-85 | active | match | 5 | unit, fixture, differential | unit, fixture | 4 | 1 |
| ENC-86 | active | diverge | 5 | unit, fixture, differential | unit, fixture, differential | 4 | 1 |
| ENC-87 | active | diverge | 5 | unit, fixture | unit, fixture | 2 | 1 |
| ENC-88 | active | diverge | 5 | unit, fixture, differential | unit, fixture, differential | 3 | 1 |
| ERR-01 | active | y-sharp-own | 11 | unit | unit | 3 | 0 |
| ERR-02 | active | y-sharp-own | 11 | unit | unit | 2 | 0 |
| ERR-03 | active | y-sharp-own | 11 | unit | unit, fixture, fuzz | 3 | 1 |
| ERR-04 | active | y-sharp-own | 11 | unit | unit, fixture | 2 | 1 |
| ERR-05 | active | y-sharp-own | 11 | unit | unit | 3 | 0 |
| ERR-06 | active | y-sharp-own | 11 | unit | unit, fixture | 4 | 1 |
| ERR-07 | active | y-sharp-own | 11 | unit | unit, fixture | 4 | 1 |
| ERR-08 | active | y-sharp-own | 11 | unit | unit | 6 | 0 |
| ERR-09 | active | y-sharp-own | 11 | unit | unit, fixture | 5 | 1 |
| ERR-10 | active | y-sharp-own | 11 | unit | unit | 3 | 0 |
| ERR-11 | active | y-sharp-own | 11 | unit | unit, fuzz | 4 | 0 |
| ERR-12 | active | y-sharp-own | 11 | unit | unit, fuzz | 4 | 0 |
| ERR-13 | active | y-sharp-own | 11 | unit | unit | 1 | 0 |
| ERR-14 | active | y-sharp-own | 11 | unit | unit | 1 | 0 |
| ERR-15 | active | y-sharp-own | 11 | unit | unit | 1 | 0 |
| ERR-16 | active | y-sharp-own | 11 | unit | unit | 2 | 0 |
| ERR-17 | active | y-sharp-own | 11 | unit | unit | 1 | 0 |
| ERR-18 | active | y-sharp-own | 11 | unit | unit | 1 | 0 |
| ERR-19 | active | y-sharp-own | 11 | unit | unit | 1 | 0 |
| ERR-20 | active | y-sharp-own | 11 | unit | unit | 3 | 0 |
| ERR-21 | active | y-sharp-own | 11 | unit | unit, fixture, fuzz | 3 | 8 |
| F-1 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 2 |
| F-2 | active | match | 7 | unit, differential | unit, port, fixture | 4 | 1 |
| F-3 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 1 |
| F-4 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 2 |
| F-5 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 2 |
| F-6 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 1 |
| F-7 | active | match | 7 | unit, port, differential | unit, port, fixture | 5 | 1 |
| F-8 | active | match | 7 | unit, port, differential | unit, port, fixture | 4 | 1 |
| F-9 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 1 |
| GC-01 | active | match | 6 | unit, differential | unit, fixture | 3 | 3 |
| GC-02 | active | match | 6 | unit, differential | unit, fixture | 2 | 1 |
| GC-03 | active | match | 6 | unit, differential | unit, fixture | 3 | 1 |
| GC-04 | active | match | 6 | unit, differential | unit, fixture | 7 | 3 |
| GC-05 | active | match | 6 | unit, differential | unit | 1 | 0 |
| GC-06 | active | match | 6 | unit, fixture, differential | unit, fixture | 4 | 3 |
| GC-07 | active | match | 6 | unit, differential | unit, fixture | 1 | 2 |
| GC-08 | active | match | 6 | unit, differential | unit, fixture | 3 | 4 |
| GC-09 | active | match | 6 | unit, differential | unit | 2 | 0 |
| GC-10 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| GC-11 | active | match | 6 | unit, differential | unit | 1 | 0 |
| HOS-AMP | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 15 | 1 |
| HOS-CLOCK | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 4 | 1 |
| HOS-DEEP | active | y-sharp-own | 11 | unit, fuzz | unit, fuzz | 10 | 0 |
| HOS-LEN | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 14 | 1 |
| HOS-MUT | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 3 | 1 |
| HOS-PROTO | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 3 | 1 |
| HOS-REF | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 7 | 1 |
| HOS-SEM | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 4 | 1 |
| HOS-TRUNC | active | y-sharp-own | 11 | unit, fuzz | unit, fixture, fuzz | 7 | 1 |
| M-1 | active | match | 7 | unit, differential | unit, port, fixture, differential | 4 | 1 |
| M-10 | active | match | 7 | unit, differential | unit, port | 1 | 0 |
| M-11 | active | match | 7 | unit, differential | unit, fixture | 1 | 2 |
| M-12 | active | match | 7 | unit, differential | unit, fixture | 2 | 1 |
| M-2 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| M-3 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 1 |
| M-4 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 1 |
| M-5 | active | match | 7 | unit, port, differential | unit, port, fixture, differential | 4 | 1 |
| M-6 | active | match | 7 | unit, differential | unit, port, fixture | 6 | 1 |
| M-7 | active | match | 7 | unit, differential | unit | 1 | 0 |
| M-8 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 1 |
| M-9 | active | match | 7 | unit, port, differential | unit, port, fixture | 5 | 1 |
| M-E1 | active | match | 7 | unit, port, differential | unit, port, fixture | 3 | 1 |
| M-E2 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 1 |
| M-E3 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| M-E4 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| M-E5 | active | match | 7 | unit, port, differential | unit, port | 1 | 0 |
| M-E6 | active | diverge | 7 | unit, port, differential | unit, port | 1 | 0 |
| M-E7 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 2 |
| PR-1 | active | port-flag | 9 | unit | unit | 1 | 0 |
| PR-2 | active | port-flag | 9 | unit | unit | 1 | 0 |
| PR-3 | active | port-flag | 9 | unit | unit | 1 | 0 |
| PR-4 | active | port-flag | 9 | unit | unit | 1 | 0 |
| PR-5 | active | port-flag | 9 | unit | unit | 1 | 0 |
| PR-6 | active | port-flag | 9 | unit | unit | 1 | 0 |
| PUD-01 | active | match | 8 | unit, fixture, differential | unit, fixture | 4 | 2 |
| PUD-02 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| PUD-03 | active | match | 8 | unit, fixture, differential | unit, fixture | 5 | 2 |
| PUD-04 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| PUD-05 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| PUD-06 | active | match | 8 | unit, port, differential | unit, port, fixture | 6 | 2 |
| PUD-07 | active | diverge | 8 | unit, differential | unit | 1 | 0 |
| PUD-08 | active | match | 8 | unit, differential | unit, fixture | 3 | 2 |
| PUD-09 | active | match | 8 | unit, differential | unit, fixture | 4 | 2 |
| Q01 | active | match | 5 | unit, fixture, differential | unit, fixture | 9 | 1 |
| Q02 | active | match | 5 | unit, fixture, differential | unit, fixture | 4 | 1 |
| Q03 | active | match | 5 | unit, fixture, differential | unit, fixture | 7 | 1 |
| Q04 | active | match | 5 | unit, fixture, differential | unit, fixture | 8 | 1 |
| Q05 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| Q06 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| Q07 | active | diverge | 5 | unit, fixture, differential | unit, fixture | 2 | 1 |
| Q08 | active | match | 5 | unit, fixture, differential | unit, fixture | 8 | 2 |
| Q09 | active | diverge | 5 | unit, differential | unit | 3 | 0 |
| Q10 | active | diverge | 7 | unit, differential | unit | 1 | 0 |
| Q11 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 4 | 1 |
| Q12 | active | diverge | 5 | unit, fixture, differential, fuzz | unit, fixture | 10 | 1 |
| Q13 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| Q14 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 2 |
| Q15 | active | diverge | 11 | unit, differential | unit, differential | 10 | 0 |
| Q16 | active | match | 5 | unit, fixture, differential | unit, fixture, differential | 4 | 1 |
| Q17 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 4 | 1 |
| Q18 | active | match | 5 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| Q19 | active | match | 5 | unit, fixture, differential | unit, fixture | 3 | 1 |
| Q20 | active | match | 6 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| Q21 | active | match | 6 | unit, fixture, differential, fuzz | unit, fixture | 2 | 2 |
| Q22 | active | diverge | 6 | unit, fixture, differential | unit, fixture | 4 | 1 |
| Q23 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 2 |
| Q24 | active | diverge | 7 | unit, differential | unit, port | 1 | 0 |
| Q25 | active | match | 10 | unit, fixture, differential | unit, fixture | 3 | 1 |
| Q26 | active | match | 7 | unit, fixture, differential | unit, fixture | 2 | 1 |
| Q27 | active | match | 7 | unit, fixture, differential | unit, port, fixture | 5 | 1 |
| Q28 | active | match | 5 | unit, fixture, differential | unit, fixture | 5 | 1 |
| Q29 | active | match | 7 | unit, differential | unit | 1 | 0 |
| Q30 | active | match | 5 | unit, fixture, differential | unit, fixture | 16 | 1 |
| Q31 | active | diverge | 5 | unit, differential | unit | 2 | 0 |
| Q32 | active | diverge | 9 | unit, differential | unit | 1 | 0 |
| RP-01 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| RP-02 | active | match | 8 | unit, port, differential | unit, port, fixture | 10 | 2 |
| RP-03 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| RP-04 | active | match | 8 | unit, fixture, differential, fuzz | unit, fixture | 5 | 2 |
| RP-05 | active | match | 8 | unit, fixture, differential, fuzz | unit, fixture | 5 | 2 |
| RP-06 | active | match | 8 | unit, port, differential | unit, port, fixture | 14 | 3 |
| RP-07 | active | match | 8 | unit, differential | unit | 1 | 0 |
| RP-08 | active | match | 8 | unit, differential | unit, port, fixture | 5 | 2 |
| RP-09 | active | match | 8 | unit, differential | unit, port, fixture | 3 | 1 |
| SIM-01 | active | diverge | 10 | unit | unit | 1 | 0 |
| SIM-02 | active | diverge | 10 | unit | unit | 2 | 0 |
| SIM-03 | active | diverge | 10 | unit | unit | 1 | 0 |
| SIM-04 | active | diverge | 10 | unit | unit | 2 | 0 |
| SIM-05 | active | diverge | 10 | unit | unit | 1 | 0 |
| SIM-10 | active | y-sharp-own | 2 | unit | unit, fixture | 11 | 12 |
| SIM-11 | active | y-sharp-own | 2 | unit | unit, port, fixture | 55 | 12 |
| SIM-12 | active | y-sharp-own | 2 | unit | unit, port, fixture | 54 | 12 |
| SIM-13 | active | y-sharp-own | 2 | unit | unit, port, fixture | 60 | 12 |
| SIM-14 | active | y-sharp-own | 2 | unit | unit, fixture | 8 | 12 |
| SIM-15 | active | diverge | 10 | unit, fixture | unit, fixture | 1 | 1 |
| SNAP-01 | active | match | 8 | unit, differential | unit, port, fixture | 4 | 4 |
| SNAP-02 | active | match | 8 | unit, fixture, differential | unit, fixture | 3 | 2 |
| SNAP-03 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SNAP-04 | active | match | 8 | unit, differential | unit, port | 2 | 0 |
| SNAP-05 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 4 |
| SNAP-06 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SNAP-07 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| SNAP-08 | active | match | 8 | unit, port, differential | unit, port, fixture | 11 | 3 |
| SNAP-09 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SNAP-10 | active | match | 8 | unit, port, differential | unit, port | 3 | 0 |
| SNAP-11 | active | match | 8 | unit, differential | unit | 2 | 0 |
| STR-01 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-02 | active | match | 6 | unit, differential | unit, fixture | 2 | 3 |
| STR-03 | active | match | 6 | unit, differential | unit | 3 | 0 |
| STR-04 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-05 | active | match | 6 | unit, differential | unit | 2 | 0 |
| STR-06 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-07 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-08 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-09 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-10 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-11 | active | match | 6 | unit, differential | unit, fixture | 1 | 2 |
| STR-12 | active | match | 6 | unit, differential | unit | 3 | 0 |
| STR-13 | active | match | 6 | unit, differential | unit, fixture | 2 | 3 |
| STR-14 | active | match | 6 | unit, differential | unit | 3 | 0 |
| STR-15 | active | match | 6 | unit, differential | unit, fixture | 2 | 2 |
| STR-16 | active | match | 6 | unit, differential | unit | 3 | 0 |
| STR-17 | active | match | 6 | unit, differential | unit, fixture | 3 | 1 |
| STR-18 | active | match | 6 | unit, differential | unit, fixture | 7 | 5 |
| STR-19 | active | match | 6 | unit, differential | unit, fixture | 6 | 4 |
| STR-20 | active | match | 6 | unit, differential | unit, fixture | 6 | 5 |
| STR-21 | active | match | 6 | unit, differential | unit, fixture, differential | 4 | 2 |
| STR-22 | active | match | 6 | unit, differential | unit, fixture | 2 | 1 |
| STR-23 | active | match | 6 | unit, differential | unit, fixture | 8 | 2 |
| STR-24 | active | match | 6 | unit, differential | unit, fixture | 2 | 1 |
| STR-25 | active | match | 6 | unit, differential | unit, fixture | 2 | 1 |
| STR-26 | active | match | 6 | unit, differential | unit, fixture | 3 | 3 |
| STR-27 | active | match | 6 | unit, differential | unit, fixture | 1 | 2 |
| STR-28 | active | match | 6 | unit, differential | unit, fixture | 2 | 2 |
| STR-29 | active | match | 6 | unit, fixture, differential, fuzz | unit, fixture | 4 | 2 |
| STR-30 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-31 | active | match | 6 | unit, differential | unit | 2 | 0 |
| STR-32 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-33 | active | match | 6 | unit, differential | unit | 2 | 0 |
| STR-34 | active | match | 6 | unit, differential | unit, fixture | 3 | 1 |
| STR-35 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-36 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-37 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-38 | active | match | 6 | unit, fixture, differential | unit, fixture | 2 | 1 |
| STR-39 | active | match | 6 | unit, fixture, differential | unit, fixture | 3 | 1 |
| STR-40 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-41 | active | match | 6 | unit, differential | unit, fixture | 1 | 2 |
| STR-42 | active | match | 6 | unit, fixture, differential, fuzz | unit, fixture | 4 | 3 |
| STR-43 | active | match | 6 | unit, fixture, differential, fuzz | unit, fixture | 3 | 1 |
| STR-44 | active | match | 6 | unit, fixture, differential, fuzz | unit, fixture | 3 | 3 |
| STR-45 | active | match | 6 | unit, differential | unit, fixture | 1 | 2 |
| STR-46 | active | match | 6 | unit, differential | unit | 2 | 0 |
| STR-47 | active | match | 6 | unit, differential | unit | 3 | 0 |
| STR-48 | active | match | 6 | unit, differential | unit, fixture | 2 | 1 |
| STR-49 | active | match | 6 | unit, differential | unit, fixture | 3 | 1 |
| STR-50 | active | match | 6 | unit, differential | unit | 2 | 0 |
| STR-51 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-52 | active | match | 6 | unit, differential | unit | 3 | 0 |
| STR-53 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-54 | active | match | 6 | unit, differential | unit, fixture | 4 | 1 |
| STR-55 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-56 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-57 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-58 | active | match | 6 | unit, differential | unit, fixture | 2 | 1 |
| STR-59 | active | match | 6 | unit, differential | unit, fixture | 1 | 1 |
| STR-60 | active | match | 6 | unit, differential | unit, fixture | 2 | 3 |
| STR-61 | active | match | 6 | unit, fixture, differential | unit, fixture | 3 | 2 |
| STR-62 | active | match | 6 | unit, fixture, differential | unit, fixture | 5 | 3 |
| STR-63 | active | match | 6 | unit, fixture, differential | unit, fixture | 6 | 3 |
| STR-64 | active | match | 6 | unit, fixture, differential | unit, fixture | 9 | 4 |
| STR-65 | active | match | 6 | unit, fixture, differential | unit, fixture | 6 | 2 |
| STR-66 | active | match | 6 | unit, fixture, differential | unit, fixture | 2 | 2 |
| STR-67 | active | match | 6 | unit, fixture, differential | unit, fixture, differential | 3 | 3 |
| STR-68 | active | match | 6 | unit, fixture, differential | unit, fixture | 2 | 1 |
| STR-69 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-70 | active | match | 6 | unit, differential | unit | 1 | 0 |
| STR-71 | active | unsafe-excluded | 6 | unit | unit, differential | 3 | 0 |
| STR-72 | active | match | 6 | unit, differential | unit | 2 | 0 |
| STR-73 | active | diverge | 6 | unit, differential | unit | 2 | 0 |
| SUB-01 | active | match | 8 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 1 |
| SUB-02 | active | match | 8 | unit, fixture, differential | unit, fixture, differential | 5 | 1 |
| SUB-03 | active | match | 8 | unit, port, differential | unit, port, fixture, differential | 6 | 1 |
| SUB-04 | active | match | 8 | unit, port, differential | unit, port, fixture, differential | 5 | 1 |
| SUB-05 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SUB-06 | active | match | 8 | unit, differential | unit, port, fixture, differential | 5 | 2 |
| SUB-07 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| SUB-08 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 2 |
| SUB-09 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SUB-10 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| SUB-11 | active | match | 8 | unit, port, differential | unit, port, fixture | 4 | 1 |
| SUB-12 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SUB-13 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SUB-14 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SY-1 | active | match | 9 | unit, fixture, differential | unit, fixture | 1 | 1 |
| SY-10 | active | match | 9 | unit, port, differential | unit, port | 2 | 0 |
| SY-11 | active | match | 9 | unit, fixture, differential, fuzz | unit, fixture, differential | 4 | 2 |
| SY-12 | active | match | 9 | unit, fixture, port, differential | unit, port, fixture, differential | 5 | 2 |
| SY-13 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| SY-14 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 5 | 2 |
| SY-15 | active | match | 9 | unit, port, differential | unit, port | 1 | 0 |
| SY-16 | active | match | 9 | unit, fixture, differential | unit, fixture | 1 | 2 |
| SY-17 | active | match | 9 | unit, differential | unit, fixture, differential | 2 | 1 |
| SY-18 | active | match | 9 | unit, differential | unit, fixture, differential | 3 | 1 |
| SY-19 | active | match | 9 | unit, fixture, differential, fuzz | unit, fixture, differential | 2 | 1 |
| SY-2 | active | match | 9 | unit, fixture, differential | unit, fixture | 1 | 1 |
| SY-20 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 3 | 1 |
| SY-3 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 3 | 1 |
| SY-4 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 3 | 3 |
| SY-5 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 3 | 1 |
| SY-6 | active | match | 9 | unit, fixture, differential | unit, fixture, differential | 3 | 1 |
| SY-7 | active | match | 9 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 2 |
| SY-8 | active | match | 9 | unit, fixture, differential, fuzz | unit, fixture, differential | 3 | 3 |
| SY-9 | active | match | 9 | unit, differential | unit, fixture | 1 | 1 |
| T-0.1 | active | match | 7 | unit, differential | unit, fixture, differential | 2 | 1 |
| T-0.10 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-0.11 | active | match | 7 | unit, differential | unit, differential | 2 | 0 |
| T-0.12 | active | match | 7 | unit, port, differential | unit, port | 2 | 0 |
| T-0.13 | active | unsafe-excluded | 7 | unit | unit | 1 | 0 |
| T-0.2 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-0.3 | active | match | 7 | unit, port, differential | unit, port | 1 | 0 |
| T-0.4 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-0.5 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-0.6 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-0.7 | active | match | 7 | unit, port, differential | unit, port | 2 | 0 |
| T-0.8 | active | match | 7 | unit, differential | unit, fixture, differential | 4 | 3 |
| T-0.9 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-1 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 3 |
| T-2 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 2 |
| T-3 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 1 |
| T-4 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| T-5 | active | match | 7 | unit, port, differential | unit, port, fixture | 1 | 2 |
| T-6 | active | match | 7 | unit, differential | unit, port, fixture | 5 | 2 |
| T-7 | active | match | 7 | unit, differential | unit | 1 | 0 |
| T-8 | active | match | 7 | unit, differential | unit, fixture | 1 | 2 |
| T-9 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| TX-1 | active | match | 7 | unit, port, differential | unit, port, fixture | 4 | 3 |
| TX-10 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 1 |
| TX-11 | active | match | 7 | unit, differential | unit | 1 | 0 |
| TX-12 | active | match | 7 | unit, differential | unit | 2 | 0 |
| TX-13 | active | match | 7 | unit, differential | unit | 2 | 0 |
| TX-14 | active | match | 7 | unit, fixture, differential | unit, port, fixture | 2 | 1 |
| TX-15 | active | match | 7 | unit, fixture, differential | unit, fixture | 1 | 1 |
| TX-16 | active | match | 7 | unit, port, differential | unit, port | 1 | 0 |
| TX-17 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| TX-18 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| TX-19 | active | match | 7 | unit, port, differential | unit, port, fixture | 4 | 1 |
| TX-2 | active | match | 7 | unit, differential | unit, port | 3 | 0 |
| TX-3 | active | match | 7 | unit, differential | unit | 3 | 0 |
| TX-4 | active | match | 7 | unit, differential | unit | 1 | 0 |
| TX-5 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 1 |
| TX-6 | active | match | 7 | unit, port, differential | unit, port | 2 | 0 |
| TX-7 | active | diverge | 7 | unit, differential | unit, port | 1 | 0 |
| TX-8 | active | match | 7 | unit, differential | unit | 1 | 0 |
| TX-9 | active | match | 7 | unit, differential | unit | 1 | 0 |
| UM-01 | active | match | 8 | unit, port, differential | unit, port, fixture | 7 | 2 |
| UM-02 | active | match | 8 | unit, port, differential | unit, port, fixture | 6 | 1 |
| UM-03 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 2 |
| UM-04 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 2 |
| UM-05 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 3 |
| UM-06 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| UM-07 | active | match | 8 | unit, differential | unit | 2 | 0 |
| UM-08 | active | match | 8 | unit, differential | unit, fixture | 3 | 2 |
| UM-09 | active | match | 8 | unit, port, differential | unit, port | 1 | 0 |
| UM-10 | active | match | 8 | unit, port, differential | unit, port, fixture | 11 | 3 |
| UM-11 | active | match | 8 | unit, differential | unit, port, fixture | 4 | 2 |
| UM-12 | active | match | 8 | unit, port, differential | unit, port, fixture | 6 | 1 |
| UM-13 | active | match | 8 | unit, differential | unit | 1 | 0 |
| UM-14 | active | match | 8 | unit, port, differential | unit, port, fixture | 4 | 1 |
| UM-15 | active | match | 8 | unit, differential | unit | 1 | 0 |
| UM-16 | active | match | 8 | unit, differential | unit, port | 4 | 0 |
| UM-17 | active | match | 8 | unit, differential | unit, port | 3 | 0 |
| UM-18 | active | match | 8 | unit, port, differential | unit, port, fixture | 9 | 1 |
| UM-19 | active | match | 8 | unit, port, differential | unit, port, fixture | 2 | 1 |
| UM-20 | active | match | 8 | unit, port, differential | unit, port, fixture | 6 | 1 |
| V-1 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 1 |
| V-2 | active | match | 7 | unit, port, differential | unit, port | 5 | 0 |
| V-3 | active | match | 7 | unit, port, differential | unit, port | 1 | 0 |
| V-4 | active | match | 7 | unit, differential | unit, port | 1 | 0 |
| V-5 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 2 |
| V-6 | active | match | 7 | unit, differential | unit, port, fixture | 2 | 1 |
| V-7 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| V-8 | active | match | 7 | unit, differential | unit, port, fixture | 3 | 1 |
| V-9 | active | match | 7 | unit, differential | unit | 1 | 0 |
| X-1 | active | match | 7 | unit, port, differential | unit, port, fixture, differential | 5 | 3 |
| X-2 | active | match | 7 | unit, differential | unit, fixture, differential | 2 | 1 |
| X-E1 | active | match | 7 | unit, differential | unit, fixture | 2 | 1 |
| X-E2 | active | match | 7 | unit, differential | unit, port, fixture, differential | 6 | 2 |
| X-E3 | active | match | 7 | unit, differential | unit | 3 | 0 |
| X-E4 | active | match | 7 | unit, port, differential | unit, port, fixture | 6 | 1 |
| X-E5 | active | match | 7 | unit, port, differential | unit, port, fixture, differential | 3 | 1 |
| X-E6 | active | match | 7 | unit, port, differential | unit, port, fixture | 3 | 1 |
| X-E7 | active | match | 7 | unit, differential | unit, fixture, differential | 2 | 1 |
| X-E8 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| Y-1 | active | match | 7 | unit, differential | unit, port, fixture | 4 | 1 |
| Y-2 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 2 |
| Y-3 | active | match | 7 | unit, differential | unit, fixture | 1 | 1 |
| Y-4 | active | match | 7 | unit, differential | unit | 1 | 0 |
| Y-5 | active | match | 7 | unit, differential | unit, port | 2 | 0 |
| Y-6 | active | match | 7 | unit, port, differential | unit, port, fixture | 2 | 1 |
| Y-7 | active | match | 7 | unit, differential | unit, fixture | 2 | 1 |
