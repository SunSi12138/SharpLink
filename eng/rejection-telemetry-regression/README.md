# Source-scoped connection rejection telemetry regression

This source-only replay validates `ConnectionAdmissionTests.RejectionTelemetryRecordsReasonedRejections`. It does not change production telemetry or disable test parallelism.

## What this establishes

The original fixture listened to the process-wide rejection meter. Three unrelated rejections could satisfy its count-only wait before its own rejected connections left `AcceptAsync`. The callback also published the count before completing the reasons list, and the assertion read that list without synchronization.

The corrected fixture:

- Carries a unique, fixture-local `AsyncLocal` marker through its server's existing `Task.Run` execution context, then resets the parent marker in `finally`.
- Counts only synchronous callbacks carrying that marker. The metric itself has no server or connection identity tag.
- Publishes count and reason tags under one local lock, joins all three local transport disposals, and asserts the original `connection_limit` reason against a locked snapshot.

`ObserverIsolation.Add` calls `Counter.Add` synchronously. The accept loop records the rejection before calling the transport's `DisposeAsync`, which publishes `DisposeCount`. The replay verifies this source-scoping behavior dynamically rather than assuming that context flows.

## Evidence boundaries

Both telemetry fixture variants run against the same production tree:

`c242f33f0041439b176c43460864a888237b79d9`

The baseline fixture is byte-identical to the one in the supplied `c60393508cecd20886e798a474f189f4faf52c62` tree. It is not a replay of historical `13928` production. Separate true-`13928` stream controls are outside this replay and must not be conflated with its baseline counterexample.

The controlled interleaving proves a legal early-readiness failure; it does not claim to reconstruct the exact ordering of any hosted failure.

Pinned fixture SHA-256 values:

- Baseline: `65b659f64ec2e1bd3212b7dfa188cc369ea735da8ce5eb58b1781426c9db2cc6`
- Corrected: `af2c47a8a7bdfb6c3725ff55b11d35db27bae1ab740cb858a3b2b2492cdb0f8f`

The replay rejects other fixture versions or production trees. Adaptation to future source changes requires reviewing the assertions and source anchors, not bypassing the identity checks.

## Portable replay

Use Python 3.10 or later and a disposable, clean Git worktree containing the correction. The exact baseline commit must already exist in that repository and have both the baseline-identical fixture and the same production tree. Supply a .NET build host that satisfies `global.json` and a separate runtime host whose only .NET 10 runtime is `Microsoft.NETCore.App 10.0.13`.

The runner may be invoked from another checkout. It records the hashes of both authored Python files, the bound checkout commit/tree, fixture identities, production-file hashes, each command, observed test counts, output hashes, and test input DLL hashes.

```sh
python3 eng/rejection-telemetry-regression/run.py \
  --repo "$CLEAN_CHECKOUT" \
  --expected-head "$EXACT_CANDIDATE_COMMIT" \
  --baseline-commit "$EXACT_BASELINE_COMMIT" \
  --expected-src-tree c242f33f0041439b176c43460864a888237b79d9 \
  --output "$NEW_EVIDENCE_DIRECTORY" \
  --build-dotnet "$BUILD_DOTNET" \
  --runtime-dotnet "$RUNTIME_DOTNET"
```

All commit/tree arguments must be full, lowercase 40-character hashes. The evidence directory must be new or empty and outside the checkout. Dirty tracked or untracked source/build inputs are rejected before mutation. Normal ignored build outputs are permitted.

The runner fixes only the test-process environment to `DOTNET_PROCESSOR_COUNT=1` and `TUNIT_MAX_PARALLEL_TESTS=8`. It does not change repository parallelism, the existing 15-second readiness boundary, test inventory, or maintainability allowances. Builds retain `-m:1 -nr:false -p:UseSharedCompilation=false`.

## Controls and required outcomes

1. Baseline handshake-noise counterexample: hold this fixture's rejected accepts while another real server emits three `handshake_limit` samples. The original wait reports ready with own dispositions zero, then its unchanged reason assertion fails. Required result: exactly one failed test, exit 2.
2. Corrected handshake-noise positive: unrelated count three leaves the scoped count zero and readiness false. Release the own accepts; exactly three own `connection_limit` samples are observed. Required result: one passed test.
3. Corrected same-reason positive: another real server emits three `connection_limit` samples outside the marker. The corrected count remains zero until its own accepts are released, then rises to three. Required result: one passed test.
4. Missing-own-publication negative: temporarily suppress the accept-loop publication only for the exact IDs `telemetry-rejected-0`, `telemetry-rejected-1`, and `telemetry-rejected-2`. The unrelated server still emits three same-reason samples. All three own transports dispose, but scoped count stays zero and the unchanged readiness oracle fails. Required result: one failed test, exit 2.
5. Restored same-reason positive: restore pristine production source, rebuild, and repeat the genuine same-reason control. Required result: one passed test.
6. Restored deliverable: restore both original source files, require the production hash map and clean Git status to match, rebuild, and run `ConnectionAdmission*`. Required result: exactly 37 passed tests, zero failures/skips.

The exact fixture and RunLoop overlays are authored by `fixture_overlay.py`. They are written only into the supplied disposable checkout and the chosen evidence directory. The fixture and temporary production mutation are restored in nested `finally` blocks, including failed builds or unexpected test outcomes. A successful replay requires rebuilt genuine positives, not just restored source text. Abrupt process termination that prevents Python cleanup still requires discarding or manually restoring the disposable checkout.

`result.json` records success only after all controls and the restored 37-test run satisfy their checks. Raw failing and successful process output remains in the chosen evidence directory; none is copied into this source-only evidence package. No raw archives or binaries are bundled.

## Validation record

Executed unchanged on 2026-10-22 in a fresh isolated worktree, with SDK 10.0.102, runtime 10.0.13, one reported processor, and TUnit parallelism eight:

- Baseline controlled counterexample: one expected failure, exit 2, at the original reason assertion.
- Corrected handshake-noise and same-reason positives: one pass each.
- Missing-own-publication negative: one expected failure, exit 2; all three own dispositions completed while scoped telemetry remained zero.
- Rebuilt restored same-reason positive: one pass.
- Restored deliverable admission suite: 37/37 passed, zero skipped.
- Restoration checks: exact fixture/production bytes and production hash map restored, clean checkout verified.
- Dirty tracked build-input guard: rejected before build or mutation; the separately injected diagnostic edit was restored and the checkout remained clean.
- Maintainability: passed, 1,309 files checked, 17 existing allowances unchanged; corrected fixture 998 LOC under its 1,000-LOC limit.

Executed authored-source SHA-256:

- `run.py`: `510f377075313c7404f7e0b0e3f4d9de97e5a39fcdad44564d9dc686f75b1e62`
- `fixture_overlay.py`: `e9ad80257ae485fa037cefbcea361b1548a3b3ada30a071e3ba7caf6addc210e`

These results are focused fixture validation, not a full-unit-suite, cross-architecture, performance, or hosted-CI acceptance claim.
