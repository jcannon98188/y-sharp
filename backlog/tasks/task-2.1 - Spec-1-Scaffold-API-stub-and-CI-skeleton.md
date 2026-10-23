---
id: TASK-2.1
title: 'Spec 1: Scaffold, API stub and CI skeleton'
status: To Do
assignee: []
created_date: '2026-10-23 22:01'
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
- [ ] #1 YSharp.slnx with src/YSharp, tests/*, bench/YSharp.Benchmarks and tools/fixture-gen per ADR 0015
- [ ] #2 Directory.Build.props and Directory.Packages.props set net10.0, nullable, warnings as errors, analyzers; InternalsVisibleTo as specified
- [ ] #3 API stub compiles and mirrors the api-sketch (members throw NotImplemented)
- [ ] #4 CI skeleton: gating (Linux Debug/Release, Windows/macOS Release), fixture drift, smoke and nightly jobs defined; submodules checked out recursively
- [ ] #5 dotnet build and dotnet test succeed on an empty gating tier
<!-- AC:END -->
