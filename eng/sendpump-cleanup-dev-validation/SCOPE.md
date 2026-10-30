# Dev-only cleanup correctness gate

This is a new correctness validation, not a resumed performance campaign and not
an assertion that SharpLink 3.0 release acceptance is complete. No TCP performance,
collector, package, merge or release operation is part of this workflow.

## Frozen source and prior evidence

- Original dev: `57f06f172d29fa5cfd4ea8e9b86a23f122df24ab`, tree
  `e5b72625377ffdcf5f5470e2ddd69be12a449d8e`.
- Candidate: `402ae37f26d1221159a6f577d985b210655e0e47`, tree
  `33bc4aff4f6d19372fd5308ed027473399e61523`. Exact chain: dev -> cleanup
  `21ea65ec14d38de6881fe2f7fc4a0d99a8d4ad2a` -> preexisting hosting-census
  repair `981a5d5816e5869859ecfea4e952e35fe519deed` -> six-case census update.
  Only the implementation commit changes product/test: SendPump + the fixture.
- Baseline: `786e9ac9ce9e6ff88959ffe28bb1ba2c8b253eae`, tree
  `6323067a4bbf05af8ca3a4f843e0dd9537f2d55e`. Its parent is original dev;
  its branch is `validation/sendpump-cleanup-dev-baseline-20261030`;
  API readback confirmed the remote commit/tree/parent after publication.
  Its only change adds the identical fixture blob
  `0573677c1d83b95c2ed75d5e8f0d395babc7d49e`. No production fix is present.
- Reused tooling: cleanup `7e2be45e7`; strict TUnit `error` baseline calibration
  `321ec6c9e9ea3120d0be7e81626fd6bd46e35e58`. Original run 40935405015 remains
  overall FAIL (candidate's older source composition passed 2311/6/TCP14; baseline
  parser expected `failed` rather than observed `error`). Baseline-only
  40937843771 calibrated that single completed-state negative and passed.
  Neither is execution evidence for this dev-only composition.

## Required scope and exact census

One runner builds three separate frozen checkouts in normal Release with fixed
SDK 10.0.102. No warning/audit suppression or modified product/test source is
applied. A SHA512-verified separate private runtime contains only .NET 10.0.14
Core/ASP.NET runtimes and no SDK; observed result runtime must be 10.0.14.

1. Fresh original-dev discovery: 2193 raw, 2188 ordinary.
2. Baseline and candidate each freshly discover exactly original raw UIDs plus
   the six frozen `new-uids.json` entries: 2199 raw, 2194 ordinary. No removals,
   replacements, duplicate UIDs or count-only substitutes are accepted. Fixture
   namespace/type/source location must match. Raw discovery stays runner-local.
3. The exact unchanged five Explicit process-worker UIDs in four source classes
   are excluded from ordinary execution. `explicit-workers.json` names each and
   why it needs its scenario driver. Source `[Explicit]` class inventory and
   fresh discovery locations are checked. They are not known-failure exclusions.
4. Old dev + fixture executes **one** calibrated causal negative UID: completed
   stale-slot cleanup with exact marker, exception type, stack origin, failure
   counters, runtime, no retry/timeout/crash. The other five cases have no
   calibrated old-dev negative expectation and are not falsely labeled negatives.
5. Candidate executes each of the six new cases once in a separate process,
   then all 2194 ordinary cases once (including those six). This deliberate
   focused-plus-full coverage is not a retry: any focused failure remains a
   final gate failure even if full-suite execution later passes. Existing #961
   cases and other ordinary tests are not filtered out. Require exact all-pass
   report census, zero skips/cancels/timeouts/flaky outcomes and retry histories,
   plus both allocation subprocess execution/runtime witnesses.
6. Every process uses a new group; timeout or leaked descendants fail the stage.
   Unquiescent groups and monitoring/cleanup errors (ps/kill/wait/poll) abort all
   subsequent execution; raw error details remain in per-process status JSON.
   A failure to write status cannot downgrade uncertain quiescence to a
   recoverable stage failure. Candidate independent
   stages still run after safe baseline/focused failures; no failed record is
   removed or replaced. All DLLs under each unit output (including nested probe
   DLLs) are SHA256-bound before/after and at final completion. Sources remain
   clean and SHA/tree/ancestry-bound. Request/tool SHA, tree, ancestry and
   cleanliness are rechecked immediately before the final gate-pass marker.

The older 14-case static-stream/TCP correctness fixture was developed on a
composition containing #931/static cancellation changes not present on dev57.
It has known expected failures for this isolated cleanup composition and is not
imported into this new cleanup-specific gate. This does **not** erase any prior
failure or claim that broader static-stream correctness has passed; those fixes
and release gates require separate validation. No perf/equivalence claim is made.

## Report isolation and retention

Each stage gets a new private child-report directory outside all source trees.
MTP's inherited `platformOptions__resultDirectory` is used, with all case/colon/
double-underscore aliases from generic configuration namespaces removed first.
Parent explicit report CLI options remain unchanged and take precedence. No
snapshot exception, post-hoc report move, deletion, upload or cache is used.
Raw reports/logs/status/identity/failure JSON remain in a fresh `/tmp/slc-*`
directory for the runner lifetime. Bounded stage/count/hash messages only reach
CI; arbitrary error text is retained locally. `GITHUB_*` reporter hooks are
removed from test environment and TUnit artifact upload explicitly disabled.
TUnit can overwrite its own same-named child reports inside a stage, as before;
this does not claim preservation of each child invocation's separate report.

Pinned source support for the environment route:
- MTP 2.5.1 commit `0bf77e3aedbf86f1fa16220bc951d3b52ba56763`,
  `src/Platform/Microsoft.Testing.Platform/Configurations/EnvironmentVariablesConfigurationProvider.cs:107`
  (`__` normalization), `EnvironmentVariablesConfigurationSource.cs` (Order 1),
  `Hosts/TestHostBuilder.CommonServices.cs:92` (default registration),
  `Configurations/AggregatedConfiguration.cs:351-405` (CLI first, then
  `platformOptions:resultDirectory`).
- TUnit 1.73.19 commit `ff2dc5186b05eddc8f6aea03a18784df3aa6e737`,
  `src/TUnit.Engine/Extensions/TestApplicationBuilderExtensions.cs:118` injects
  the result directory into the HTML reporter; `Reporters/Html/HtmlReporter.cs`
  `GetDefaultOutputPath` and `GetSidecarPath` place the HTML and JSON there.

Pure Python tests model the orchestration and reject malformed provenance,
corpus/status/marker/runtime/identity/alias/process cases. They do not execute
MTP/CLR/collector/MSBuild IPC and do not prove hosted runtime behavior. Exact
local tool HEAD must receive independent review before a request-only child
commit is pushed to the one named validation branch. Attempt 1 only; no silent
reruns or rewriting historical results. This workflow does not open a PR.
