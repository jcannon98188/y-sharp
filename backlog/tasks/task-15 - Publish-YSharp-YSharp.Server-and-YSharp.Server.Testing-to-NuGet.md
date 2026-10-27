---
id: TASK-15
title: 'Publish YSharp, YSharp.Server and YSharp.Server.Testing to NuGet'
status: To Do
assignee: []
created_date: '2026-10-27 01:02'
labels:
  - release
  - packaging
dependencies: []
ordinal: 71000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Nothing is published yet: hosts can only use YSharp from source. ADR 0024 SRV-3 says the core and the server tier ship as separate packages (YSharp, YSharp.Server; YSharp.Server.Testing carries the storage conformance suite for backend authors), and YSharp.Server already declares PackageId and Version 0.1.0 while the core has no package metadata. The repository is MIT (LICENSE, THIRD-PARTY-NOTICES.md for Yjs, lib0 and y-protocols; PackageLicenseExpression in Directory.Build.props). Publishing needs a nuget.org API key, which only the owner can create and store as a GitHub Actions secret. Open API decisions in docs/api-decisions.md (section E) change the public surface and should be settled before a first non-prerelease version.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 dotnet pack produces YSharp, YSharp.Server and YSharp.Server.Testing packages with id, version, description, authors, repository URL, MIT license expression, a readme and the third-party notices; YSharp.Server depends on YSharp with a lower bound only (SRV-3)
- [ ] #2 Packages carry symbols (snupkg) and Source Link, and builds are deterministic in CI
- [ ] #3 A GitHub Actions workflow packs on every push and publishes to nuget.org only from a version tag, using a NUGET_API_KEY secret, after the gating suite passes
- [ ] #4 The version scheme and how a release is cut are documented (README or docs), including prerelease versions until the section E API decisions are settled
- [ ] #5 A first prerelease of all three packages installs into a fresh console project and a doc round-trips an update
<!-- AC:END -->
