# SharpFuzz harness (ADR 0013)

A coverage-guided fuzz harness for y-sharp, run as a non-gating nightly job (`.github/workflows/sharpfuzz.yml`). It is not part of
`YSharp.slnx`; the gating build never restores SharpFuzz.

One fuzz input is a mode byte plus a payload: 0 V1 update, 1 V2 update, 2 sync message stream, 3 awareness update, 4 state vector.
The harness fails (crashes the process) on an unexpected exception, a faulted document, state that cannot be encoded and re-applied, or a
known-good update that stops applying. Hangs, out-of-memory and stack overflows are what the fuzzer already reports.

```
dotnet build tools/sharpfuzz/YSharp.SharpFuzz.csproj -c Release -o fuzz-out
cp -r fuzz-out fuzz-replay                                  # an uninstrumented copy for replay and minimise
dotnet tool install --global SharpFuzz.CommandLine
sharpfuzz fuzz-out/YSharp.dll                              # instrument the library
dotnet fuzz-replay/YSharp.SharpFuzz.dll --export-seeds seeds  # fixtures/hostile as AFL seeds
afl-fuzz -i seeds -o findings -t 5000 -m none -- dotnet fuzz-out/YSharp.SharpFuzz.dll
```

Reproduce and minimise a finding with the uninstrumented copy (the instrumented `YSharp.dll` only runs under afl-fuzz), then commit it.
The nightly job does this for every finding and uploads `sharpfuzz-findings`: per finding the raw input, its minimised form and the replay output:

```
dotnet fuzz-replay/YSharp.SharpFuzz.dll --replay findings/default/crashes/id:000000,...
dotnet fuzz-replay/YSharp.SharpFuzz.dll --minimise findings/default/crashes/id:000000,... minimised.bin
```

Drop the mode byte, add the bytes as one case under the matching `HOS-*` class in `tools/fixture-gen/scenarios/hostile-corpus.mjs`
(with `discovered: 'YYYY-MM-DD'`), run `npm run gen` in `tools/fixture-gen`, and commit the regenerated `fixtures/hostile/*.json`. The gating suite
replays every file in that folder, so the new case is the regression test.
