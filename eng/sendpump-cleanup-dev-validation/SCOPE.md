# One ordinary-only dev cleanup diagnostic

## Purpose and retained results

This corrects an incompatible **tool assumption**, not the product or test limits.
It executes one ordinary suite on exact product
`402ae37f26d1221159a6f577d985b210655e0e47`, tree
`33bc4aff4f6d19372fd5308ed027473399e61523`. The same three-commit dev-only source
ancestry and fixture blob remain enforced. No baseline or six-focused campaign
is repeated; no TCP/performance work, package, merge or release action is included.

- Run 40984593149 attempt1 remains FAIL with zero product execution (tool/request
  self-test layout error). The corrected real request-layout tests remain.
- Run 40984965648 attempt1 remains overall FAIL. Its fresh dev2193 ->
  baseline/candidate2199 exact-six UID difference, single completed-state causal
  baseline, all six candidate focused cases and final source/DLL checks passed.
  Its ordinary report reached observed runtime .NET10.0.14, then a GateError;
  detailed stage was unavailable. We do **not** retroactively claim its ordinary
  validator passed or that a specific failed test was absent.
- The old ordinary oracle imported `READ_OWNERSHIP_ALLOCATION_VERIFIED` subprocess
  witnesses from the #931-containing source. Dev's two allocation tests instead
  are synchronous, parameterless `[NotInParallel]` methods with original exact
  zero-allocation and Gen2 guards. Such child witnesses cannot occur on this
  source. Requiring them was a tooling scope mistake, not product evidence.
- Standard #964 snapshot and terminal artifacts (runs40983957912/40983958328)
  independently contain eight current-full reports with 2194 passed, all other
  counters zero, runtime10.0.14, both synchronous allocation UIDs passed and no
  child markers. They ran synthetic merge45f5785c7effe061129ae92aa7750222ec67323c
  with concurrency8; they do not replace this concurrency4 execution or explain
  the old special run's exact failing stage.

## Unchanged execution contract

Normal Release build, fixed SDK10.0.102, SHA512-verified private .NET10.0.14
runtime, concurrency4, ordinary minimum2194, timeout20m plus external1260s,
strict zero-tests policy and all original assertions/thresholds are unchanged.
There is no skip/known-failure/Gen2 exception, result replacement or retry.

Fresh discovery must match the **already observed** run40984965648 digests:
- raw2199: `b1ed36e5939aedfeb5f97598dbb38481682082addb4e640d1345c3e0873c2514`
- ordinary2194: `d4ed0e528f0f76a629cba2ecc9d1e4f2640eac4fa13cf88100d69ed4eb0288b3`

The unchanged five source-reviewed Explicit workers are individually disclosed
in `explicit-workers.json`, verified against exact class/source/discovery
identity, and not invoked without their separate scenario drivers. No fresh
baseline rebuild is needed to reconfirm the already-established six-case delta.

The original allocation source file SHA256 is pinned to
`c538ee7fb5ca4a2325b5062bd867382bc05c73526b54f7a638ae15ef0d07fa21`.
Both original synchronous UIDs, source locations, `[NotInParallel]`, exact-zero,
Gen2 and registered-consumer assertions are checked. They must appear once and
pass in the ordinary report. The wrong #931 parameterized/child-probe fixture is
rejected; no production/test source is changed to satisfy an oracle.

All exact UID/schema/status/retry/counter/exit acceptance checks from the previous
ordinary validator remain; only fixed observational stage callbacks are added.
The full SHA-bound DLL census and clean source/tool/request checks remain. Every
started process must establish group quiescence: ps/wait/kill/poll errors are
fatal, raw local status is retained, and no subsequent product process runs.

## Reporting and publication

Each discovery/execution has private inherited child-report paths using the
fixed MTP2.5.1 `platformOptions__resultDirectory` route; generic env aliases are
cleared. Parent CLI paths are unchanged. Reports are not moved, removed or
silently ignored. Original test reporting behavior remains otherwise unchanged.

Bounded stdout diagnostics expose stage, actual counts, known failed UIDs (up to
16) or unknown-UID hashes, runtime and process exit. `diagnostic.json` also keeps
exact exception details. The ordinary HTML/JSON test report and diagnostic JSON
are explicitly authorized **temporary Actions artifacts**, uploaded even after
failure with retention3 days. No raw build log, whole output directory, extra
evidence repository, cache or package is uploaded. Failed validators still fail
the run. Artifact availability is not evidence that correctness passed.

Before publishing: independent exact tool HEAD review; pure controls on tool-only
and real request-only trees; then a request-only child binding actual remote
SHA/tree. New branch `validation/sendpump-cleanup-dev-ordinary-diagnostic-20261030`,
attempt1 only. Never rerun, replace or reclassify either prior failed run.
