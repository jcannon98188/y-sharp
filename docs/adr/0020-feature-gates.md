# Declared feature gates replace the global stub probe

Extends ADR 0019 and the ADR 0018 milestone amendment. Today every test that touches the library skips through one probe: `ApiStub.IsStub` checks whether `YValue.Null` throws `NotImplementedException`. When that is true, a `NotImplementedException` from any member turns into a skip. The probe flips as soon as `YValue` is implemented in M1, and every area that has not landed yet then fails instead of skipping. Per-area runtime probes would fix that but would hide a real gap inside an area that has landed, which is the failure the design exists to prevent. So tests declare what they need instead.

## Decision

- **Features.** A feature is a capability the size of one plan spec, such as `Values`, `Store`, `V1`, `V2`, `Text`, `Array`, `Map`, `Xml`, `Events`, `Undo` or `Awareness`. Each milestone is a named set of features. The feature list is taken from the packaged implementation plan, and each spec delivers exactly the features it names.
- **Declared requirements.** A test declares the features it needs (`[Requires(Feature.Text)]`). The default is set at class or support-class level, and a method adds what it needs on top, for example a text test that also replays V2 bytes. A test that needs several features names all of them.
- **One landed list.** A TestKit source file (`Features.Landed`) lists the features that have landed. A spec adds its features in the same change that lands them. A typo does not compile. The traceability matrix reports the landed list.
- **Skip before running.** If any required feature has not landed, the test is skipped with the missing features named, without running its body. Otherwise it runs, and a `NotImplementedException` is a failure like any other exception. `ApiStub.IsStub`, `ApiStub.Run` and every catch that turns `NotImplementedException` into a skip are removed. That includes the fuzz `Stub`, the canary's `Stub` verdict and the interop `SharpSide` stub status, which use the same mechanism.
- **Checker rule.** The traceability gate (ADR 0010) requires every test that touches the library to have a feature requirement, directly or inherited. The exceptions are tests explicitly marked library-independent (the TestKit self-tests and the traceability tests).
- **Release.** The release gate requires every feature to be landed. The existing zero-skip rule catches a test tagged with the wrong feature.

## Considered and rejected

- **Per-area runtime probes.** Rejected because a stray `NotImplementedException` inside a landed area would still skip.
- **Gates per milestone.** Rejected because M4's specs run in parallel. With milestone-level gates, the first M4 spec to land would turn every other M4 area red on `main`.
- **An inventory YAML or an environment variable for the landed list.** Rejected as untyped. The environment variable also lets one run differ from another.

## Consequences

The plan's first spec, "Feature gates", is in M0 alongside the spike and blocks M1. It runs against the stub, tags every test, adds the checker rule and removes the old probe. The skip count is unchanged and the suite is still green when it finishes.
