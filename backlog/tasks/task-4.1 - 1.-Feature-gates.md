---
id: TASK-4.1
title: 1. Feature gates
status: To Do
assignee: []
created_date: '2026-10-24 19:41'
labels:
  - spec
  - 'model:sonnet'
milestone: m-0
dependencies: []
parent_task_id: TASK-4
ordinal: 1000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today one global probe (`ApiStub.IsStub` on `YValue.Null`) turns every `NotImplementedException` into a skip. The first implemented member flips it, so every area not yet implemented would fail. ADR 0020 replaces it with declared, spec-sized feature gates. This must land before any library code.

**Owns:** `tests/YSharp.TestKit/Stubs/` (replaced by the feature-gate code), the support classes' `Run` helpers in every test project, `tests/YSharp.Fuzz.Tests/Support/Stub.cs`, `tests/YSharp.Fuzz.Canary`, the interop `SharpSide` stub status, the traceability checker rule
**Test files:** every test project; `TestKit/ApiStubTests.cs` is replaced by feature-gate self-tests
**Suggested model:** sonnet (override freely)

Read first: `docs/handoff-implementation-planning.md`, ADRs 0005, 0006, 0008, 0018 (amendments), 0020, 0021, 0022, and the research notes for this area.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 `Feature` enum lists exactly the features named in the TASK-4 plan specs; `Features.Landed` is empty
- [ ] #2 Every test that touches the library carries a feature requirement (class or method level) matching the spec that owns its behaviour; a test needing several features names all of them
- [ ] #3 A test whose features have not landed skips before running, naming the missing features; `ApiStub.IsStub`, `ApiStub.Run` and every NotImplementedException-to-skip catch (fuzz Stub, canary, interop SharpSide) are removed
- [ ] #4 The traceability checker fails on a library-touching test with no feature requirement, except tests marked library-independent; the matrix reports the landed list
- [ ] #5 Against the stub the skip count is unchanged and the suite is green in Debug and Release
- [ ] #6 The traceability gate passes and no pending registry entry is added
<!-- AC:END -->

## Definition of Done
<!-- DOD:BEGIN -->
- [ ] #1 `dotnet build YSharp.slnx` has 0 warnings
- [ ] #2 `dotnet test --solution YSharp.slnx` is green in Debug and in Release
- [ ] #3 Fixture drift check (`npm run check` in `tools/fixture-gen`) is clean
- [ ] #4 ADR 0022 followed: suite changes in separate `suite:` commits, a Suite changes list in the notes, no open owner decision
<!-- DOD:END -->
