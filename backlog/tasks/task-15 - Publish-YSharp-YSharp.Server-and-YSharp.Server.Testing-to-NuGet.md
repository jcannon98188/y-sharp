---
id: TASK-15
title: 'Publish YSharp, YSharp.Server and YSharp.Server.Testing to NuGet'
status: In Progress
assignee:
  - '@claude'
created_date: '2026-10-27 01:02'
updated_date: '2026-10-27 04:58'
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
- [x] #1 dotnet pack produces YSharp, YSharp.Server and YSharp.Server.Testing packages with id, version, description, authors, repository URL, MIT license expression, a readme and the third-party notices; YSharp.Server depends on YSharp with a lower bound only (SRV-3)
- [x] #2 Packages carry symbols (snupkg) and Source Link, and builds are deterministic in CI
- [x] #3 A GitHub Actions workflow packs on every push and publishes to nuget.org only from a version tag, using a NUGET_API_KEY secret, after the gating suite passes
- [x] #4 The version scheme and how a release is cut are documented (README or docs), including prerelease versions until the section E API decisions are settled
- [ ] #5 A first prerelease of all three packages installs into a fresh console project and a doc round-trips an update
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Package metadata in src/Directory.Build.props (one version, readme, license, notices, XML docs); nothing outside src packable. 2. Symbols (snupkg), Source Link, ContinuousIntegrationBuild. 3. release.yml: pack on every push, smoke-install, gate and publish on v* tags. 4. docs/releasing.md. 5. Owner adds NUGET_API_KEY and tags the first preview.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implemented in c1dc3df.

Decisions:
- One version for all three packages (VersionPrefix in src/Directory.Build.props). SRV-3 allows separate versions; one keeps a release a single tag. YSharp.Server depends on YSharp >= its version (lower bound only, checked in the packed nuspec).
- XML docs ship with CS1591 (missing comment) suppressed for src: 630 public members have none. The real doc errors (CS0419 ambiguous crefs from the TASK-14 overloads, CS1734 stale paramrefs, CS1573 missing params) are fixed.
- Source Link comes from the SDK (built in since .NET 8); no package reference needed.
- release.yml calls gating.yml (now workflow_call) on a tag; the tag's version must start with VersionPrefix. Branch builds pack <prefix>-ci.<run> and upload an artifact, never publish.
- tools/package-smoke: installs the packed packages into a fresh console project outside the repo (private NUGET_PACKAGES) and checks a doc round-trip, a server edit and read, and the in-memory conformance suite.

Validation: packed locally, nuspec and contents inspected; smoke passed (18 conformance checks) at 0.1.0-dev and 0.1.0-local.1; full gated suite 5055, 0 failed; API surface unchanged.

AC5 waits on the owner: add the NUGET_API_KEY secret and push a v0.1.0-preview.1 tag on main; then verify the published packages install. Left In Progress until then.
<!-- SECTION:NOTES:END -->
