---
id: TASK-2.1
title: 'Spec 1: Scaffold, API stub and CI skeleton'
status: Done
assignee:
  - '@claude'
created_date: '2026-10-23 22:01'
updated_date: '2026-10-23 22:10'
labels: []
dependencies: []
references:
  - docs/adr/0015-test-project-layout-tooling-and-ci.md
  - docs/adr/0008-public-api-shape.md
  - docs/prototype/api-sketch
parent_task_id: TASK-2
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wave 0. Everything else compiles against this. Turn the throwaway API sketch into a compiling stub and lay out the solution per ADR 0015.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 YSharp.slnx with src/YSharp, tests/*, bench/YSharp.Benchmarks and tools/fixture-gen per ADR 0015
- [x] #2 Directory.Build.props and Directory.Packages.props set net10.0, nullable, warnings as errors, analyzers; InternalsVisibleTo as specified
- [x] #3 API stub compiles and mirrors the api-sketch (members throw NotImplemented)
- [x] #4 CI skeleton: gating (Linux Debug/Release, Windows/macOS Release), fixture drift, smoke and nightly jobs defined; submodules checked out recursively
- [x] #5 dotnet build and dotnet test succeed on an empty gating tier
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Gating smoke test first (red): tests/YSharp.Tests references YSharp stub, asserts public API types exist and members throw NotImplementedException; InternalsVisibleTo check.
2. Add .gitignore, global.json? (no), Directory.Build.props, Directory.Packages.props, YSharp.slnx.
3. Copy api-sketch into src/YSharp (stub), keep docs/prototype sketch untouched.
4. Empty shells: YSharp.Interop.Tests, YSharp.Fuzz.Tests, YSharp.TestKit, bench/YSharp.Benchmarks, tools/fixture-gen (Node package shell).
5. GitHub Actions workflows: gating matrix, fixture-drift, smoke, nightly; recursive submodules.
6. dotnet build + dotnet test; record evidence; finalize.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Stub copied from api-sketch with only CS0067 suppressed in src (undeclared-use events); AnalysisMode=Default because Recommended flags CA1822 on every throwing stub member. global.json pins SDK 10.0.x and selects MTP runner for 'dotnet test --solution'. Interop/Fuzz shells use --ignore-exit-code 8 (zero tests). Validation: dotnet build Debug+Release 0 warnings/errors; dotnet test --solution YSharp.slnx: 3 assemblies passed, 3 smoke tests green (initial run red on wrong type name). Workflows not executed (no GitHub runner); YSHARP_TIER filter itself is not implemented here.
<!-- SECTION:NOTES:END -->

## Final Summary

<!-- SECTION:FINAL_SUMMARY:BEGIN -->
Added YSharp.slnx, Directory.Build.props/Packages.props (net10.0, nullable, TWAE, analyzers), global.json, API stub in src/YSharp mirroring api-sketch, InternalsVisibleTo to Tests/Fuzz.Tests/Benchmarks only, empty TestKit/Interop/Fuzz/Benchmarks/fixture-gen shells, and four GitHub Actions workflows (gating matrix, fixture drift, smoke, nightly; recursive submodules). Verified: dotnet build (Debug+Release) clean, dotnet test passes with 3 smoke tests (surface, NotImplemented, InternalsVisibleTo list). Workflows unexecuted locally.
<!-- SECTION:FINAL_SUMMARY:END -->
