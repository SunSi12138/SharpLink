# Frozen bootstrap-supervision correctness gate

This is a **dormant correctness-only** hosted workflow. No benchmark, timing
comparison, native instrumentation, suite filtering, test retry or local sandbox
permission bypass is part of this tool.

## Frozen source and census

The product checkout is separate from the tooling checkout:

- Reviewed local product: `e9f81d9a543b2f3368b54ba5d5c3a5068b5d5fdc`.
- Published product: `13e75f15f16cb98960c97f9500e17d26ba81f43b`.
- Exact shared product tree: `025dba3a4756227e062eab005b2693fc24a62f6a`.
- SDK: exactly `10.0.102`; Release unit project and its dependencies; zero build
  warnings/errors; single MSBuild node, no reusable/shared build servers.
- Ordinary unfiltered unit run: exactly **2214 passed, 0 failed, 0 skipped**.
  Cancelled, timed-out, flaky and unknown outcomes fail too.
- Raw discovery has **2219 unique UIDs**, including the five pre-existing
  `[Explicit]` process-worker tests below. Normal unfiltered execution does not
  select these workers. They are not executed or counted as passing/skipped.
- Discovery minus precisely these five UIDs must equal the 2214 executed UIDs.
  The sorted, newline-terminated ordinary UID corpus must additionally hash to
  `9f738e6133dab2a992ba86cc8b09f21198babacfeb13a1f3ff15e246551d58c3` (SHA256),
  independently extracted from the frozen local full-run per-case report.

The full UID strings are pinned in `census.py`; their class/method/source rationale:

| Class / method (under `SharpLink.UnitTests.Validation`) | Source under `test/SharpLink.UnitTests/Validation/` | Why explicit |
| --- | --- | --- |
| `AdmissionDiagnosticsValidationProbe.Run` | `AdmissionDiagnosticsValidationProbe.cs` | Requires isolated diagnostics driver scenario |
| `CodecValidationProbe.DateTimeCrossZone` | `CodecValidationProbe.cs` | Process-global TZ experiment requires driver |
| `CodecValidationProbe.DateTimeOffsetFragmentation` | `CodecValidationProbe.cs` | Codec process worker requires driver inputs |
| `DateTimeOffsetPerformanceValidationProbe.Run` | `DateTimeOffsetPerformanceValidationProbe.cs` | Timing experiment, not ordinary correctness suite |
| `PendingLifecycleValidationProbe.Run` | `PendingLifecycleValidationProbe.cs` | Requires isolated lifecycle driver/watchdog |

The existing local full result remains **2186 passed + 28 environment failures**,
not a pass: local sandbox Unix sockets/named pipes raised `SocketException (13)`.
Original logs/reports stay under `/workspace/shared/bootstrap-supervision-20261028`;
this tool neither rewrites nor reclassifies that evidence. No local full retry.

## Review and one bounded request

1. Review the exact frozen product and the exact committed tool SHA/tree. Publish
   the dormant tool commit without the request message. Publication alone does not
   run the job: its exact message check fails and `state` is still `dormant`.
2. Only after independent review, make one single-parent child commit changing
   **only** `eng/bootstrap-correctness/REQUEST.json`: set `state` to `authorized`,
   `reviewedToolCommit` to its actual parent SHA, and `reviewedToolTree` to that
   parent's actual tree. Preserve campaign and source pins.
3. Use exactly `run: bootstrap supervision full correctness C1` as the commit
   message and push only `codex/bootstrap-correctness-validation`.
4. The guard requires this repository, branch, push event, head identity, exact
   message, exact single changed path, clean checkout, product pins and first run
   attempt. There is no dispatch/PR trigger. Rerunning an existing run is rejected.
   A tool revision requires a fresh independent exact-identity review, not reuse
   of an earlier review.

The first workflow step initializes evidence before checkout. `always()` uploads
raw restore/build logs, SDK/runner/source identity, binary hashes, discovery JSON,
full test console, TUnit per-case JSON/HTML, TRX, command/exit records, census and
failure diagnostics. A failure is never replaced by a later successful attempt.
Artifacts include request SHA, run ID and attempt in their name.

## Offline checks

```sh
python3 eng/bootstrap-correctness/test_tools.py
actionlint .github/workflows/bootstrap-correctness.yml
git diff --check
```

Tests cover missing/extra/duplicate/substituted discovered and executed UIDs,
explicit allowlist drift, every bad outcome, TRX/console/count mismatches, build
warnings, wrong event/repository/branch/attempt/message, dirty checkouts and tool
changes hidden in a request. They do not execute product tests.
