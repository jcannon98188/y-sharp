# Releasing to NuGet

y-sharp ships three packages, with one version shared by all three:

| Package | What it is |
| --- | --- |
| `YSharp` | The core library. |
| `YSharp.Server` | The server tier. It depends on `YSharp` with a lower bound only (ADR 0024, SRV-3). |
| `YSharp.Server.Testing` | The storage conformance suite, for authors of `IUpdateStore` backends. |

Only the projects under `src/` are packable. Their package metadata is in `src/Directory.Build.props`. Each package carries:

- its own `README.md`;
- `LICENSE` and `THIRD-PARTY-NOTICES.md`;
- the XML documentation;
- a symbol package (`.snupkg`) with Source Link, so a debugger steps into the exact commit's sources.

CI builds are deterministic (`ContinuousIntegrationBuild`).

## Versions

Versions follow [Semantic Versioning](https://semver.org/).

- **`VersionPrefix`** in `src/Directory.Build.props` is the version being worked towards, for example `0.1.0`.
- **Local builds** are `<prefix>-dev`.
- **CI builds** of a branch are `<prefix>-ci.<run number>`. They are packed and uploaded as a workflow artifact, never published.
- **A release** is a tag `v<version>`, and the version must start with the prefix: `v0.1.0-preview.1`, `v0.1.0-preview.2`, then `v0.1.0`. The workflow refuses a tag that does not match, so every published version can be rebuilt from its commit.

Releases stay below 1.0, with `-preview.N` prereleases, until the public API is declared stable. The section E decisions in [api-decisions.md](api-decisions.md) are settled, but before 1.0 a minor version may still change the API. The API baselines in `tools/api-surface/` record every change.

## Cutting a release

Publishing uses nuget.org [trusted publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing), so no API key is stored anywhere. The workflow asks GitHub for an OIDC token, and nuget.org exchanges it for an API key that lasts one hour. Set this up once:

1. On nuget.org, under your account's Trusted Publishing page, add a GitHub Actions policy:
   - Repository Owner `jcannon98188`;
   - Repository `y-sharp`;
   - Workflow File `release.yml` (the file name only, no path);
   - Environment left empty.
2. In the repository's Settings, Secrets and variables, Actions, add the secret `NUGET_USER`. Its value is your nuget.org profile name, not your email address.

The first push creates the three package IDs under the account that owns the policy.

1. Merge everything for the release into `main`, and make sure the gating workflow is green there.
2. If the release moves to a new version, change `VersionPrefix` in `src/Directory.Build.props` in a normal PR.
3. Tag the commit on `main` and push the tag:

   ```bash
   git tag v0.1.0-preview.1
   git push origin v0.1.0-preview.1
   ```

4. The `release` workflow (`.github/workflows/release.yml`) then:
   1. packs the three packages at the tag's version;
   2. installs them from the packed files into a fresh console project and runs `tools/package-smoke/Program.cs`, which round-trips an update, serves an edit, and runs the in-memory conformance suite;
   3. runs the full gating suite on Linux, Windows and macOS;
   4. pushes the packages and their symbols to nuget.org, but only if every step above passed.
5. Check the packages on nuget.org. Indexing takes a few minutes. Then, if you want release notes, create a GitHub release from the tag.

A version on nuget.org can be unlisted but never replaced. To fix a bad release, publish the next version.

## Checking the packages locally

```bash
for p in YSharp YSharp.Server YSharp.Server.Testing; do dotnet pack src/$p -c Release -o artifacts; done
tools/package-smoke/smoke.sh artifacts 0.1.0-dev
```

The smoke script builds its project outside the repository, with a private package cache, so it sees the packages as a user would.
