# Spec 8 traceability matrix (TASK-2.8)

Extract of the gating traceability matrix (ADR 0010) for the 81 entries Spec 8 owns: DOC-01..18, SUB-01..14, UM-01..20, SNAP-01..11, RP-01..09, PUD-01..09. The Tests and Fixtures columns are counts or lists as the gate writes them.

Result at hand-off: all 81 active, 0 pending, 0 excluded. The 58 owned ledger rows (doc, undo-redo, snapshot, relativePositions) are all ported. PUD-07 is a diverge entry (ADR 0003) with a [Diverges] test. Differential and fuzz layers are declared and reported, not gated.

| ID | Status | Kind | Owner | Declared layers | Covered layers | Tests | Fixtures |
|---|---|---|---|---|---|---|---|
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
| DOC-15 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| DOC-16 | active | match | 8 | unit, differential | unit, port, fixture | 3 | 2 |
| DOC-17 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| DOC-18 | active | match | 8 | unit, differential | unit | 2 | 0 |
| PUD-01 | active | match | 8 | unit, fixture, differential | unit, fixture | 4 | 2 |
| PUD-02 | active | match | 8 | unit, differential | unit, fixture | 3 | 1 |
| PUD-03 | active | match | 8 | unit, fixture, differential | unit, fixture | 5 | 2 |
| PUD-04 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| PUD-05 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| PUD-06 | active | match | 8 | unit, port, differential | unit, port, fixture | 6 | 2 |
| PUD-07 | active | diverge | 8 | unit, differential | unit | 1 | 0 |
| PUD-08 | active | match | 8 | unit, differential | unit, fixture | 3 | 2 |
| PUD-09 | active | match | 8 | unit, differential | unit, fixture | 4 | 2 |
| RP-01 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| RP-02 | active | match | 8 | unit, port, differential | unit, port, fixture | 10 | 2 |
| RP-03 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| RP-04 | active | match | 8 | unit, fixture, differential, fuzz | unit, fixture | 5 | 2 |
| RP-05 | active | match | 8 | unit, fixture, differential, fuzz | unit, fixture | 5 | 2 |
| RP-06 | active | match | 8 | unit, port, differential | unit, port, fixture | 14 | 3 |
| RP-07 | active | match | 8 | unit, differential | unit | 1 | 0 |
| RP-08 | active | match | 8 | unit, differential | unit, port, fixture | 5 | 2 |
| RP-09 | active | match | 8 | unit, differential | unit, port, fixture | 3 | 1 |
| SNAP-01 | active | match | 8 | unit, differential | unit, port, fixture | 4 | 4 |
| SNAP-02 | active | match | 8 | unit, fixture, differential | unit, fixture | 3 | 2 |
| SNAP-03 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SNAP-04 | active | match | 8 | unit, differential | unit, port | 2 | 0 |
| SNAP-05 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 4 |
| SNAP-06 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SNAP-07 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SNAP-08 | active | match | 8 | unit, port, differential | unit, port, fixture | 11 | 3 |
| SNAP-09 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SNAP-10 | active | match | 8 | unit, port, differential | unit, port | 3 | 0 |
| SNAP-11 | active | match | 8 | unit, differential | unit | 2 | 0 |
| SUB-01 | active | match | 8 | unit, fixture, differential, fuzz | unit, fixture | 2 | 1 |
| SUB-02 | active | match | 8 | unit, fixture, differential | unit, fixture | 4 | 1 |
| SUB-03 | active | match | 8 | unit, port, differential | unit, port, fixture | 5 | 1 |
| SUB-04 | active | match | 8 | unit, port, differential | unit, port, fixture | 4 | 1 |
| SUB-05 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SUB-06 | active | match | 8 | unit, differential | unit, port, fixture | 4 | 2 |
| SUB-07 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| SUB-08 | active | match | 8 | unit, port, differential | unit, port, fixture | 4 | 2 |
| SUB-09 | active | match | 8 | unit, differential | unit | 1 | 0 |
| SUB-10 | active | match | 8 | unit, port, differential | unit, port, fixture | 3 | 1 |
| SUB-11 | active | match | 8 | unit, port, differential | unit, port, fixture | 4 | 1 |
| SUB-12 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SUB-13 | active | match | 8 | unit, differential | unit, fixture | 2 | 1 |
| SUB-14 | active | match | 8 | unit, differential | unit | 1 | 0 |
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
