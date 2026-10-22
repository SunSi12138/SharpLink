# Cooperative maintenance: validation and acceptance boundary

This is a draft production candidate for the deliberately restricted capability in
[ADR 0004](adr/0004-cooperative-session-maintenance.md), not completion of every
requirement in #775. It is not a zero-overhead or throughput-equivalence claim.

## Composition

The PR targets #928 (`37b92f20e20ce8a7b751660d56d636a0ce1660e7`) without merging it.
It includes the physical-work prerequisite from #931 (final reviewed branch
`5d33282d65958a41db3d019702b7cca56270b352`) and intrinsic-send prerequisite from #932
(`f4be372ae4b013c767eb7f5eb0e43aad1cdb41fe`), then adds prepared replacement,
authenticated fixed-cohort maintenance, deferred cancellation-tail ownership and
transport shutdown ordering. Their individually green workflows do not prove this
combined tree; the feature runs complete validation again.

The earlier reachable source checkpoint
[`fcd9fad97cb354a67ce3c0bbb1a8adc8105ebe99`](https://github.com/SunSi12138/SharpLink/commit/fcd9fad97cb354a67ce3c0bbb1a8adc8105ebe99)
is deliberately a **known-negative counterfactual**, not a deployable version. The
final branch includes the subsequent physical transport correction.

#930 and the queued rejection-handoff experiment are **not included**. Ordinary
queued RPC behavior is unchanged. This capability rejects every current, retained,
or physically reclaiming queue-capable admission generation at opt-in and freezes
configuration. The reviewed [reader-prefix argument](maintenance-reader-prefix-proof.md)
and twenty-nine source-region pins are conservative review guards, not an execution of
the excluded queued-handoff fixture or a formal proof of arbitrary future changes.

## Final local source-bound checks

Production subtree: `3bf0a1eb6b162016d07f9d9329b1b3ca560f68a3`.
The only production delta from the previously measured `8915` tree is the cold
[absolute Stop-budget adapter](server-stop-absolute-budget.md).
Build SDK 10.0.102, Release, zero warnings/errors. Tests below ran with official
runtime 10.0.13, one reported processor and TUnit parallelism eight.

- Full unit suite: **2,412 executed, 2,384 passed, 28 failed**, no skipped,
  cancelled or timed-out tests. The exact 28 case identities match the previous
  isolated prerequisite/environment run: 26 Unix socket/named-pipe permission
  denials and two expected-exception assertions receiving that socket denial.
  Categories are shared-memory control (7), pipeline (4), named-pipe read ownership
  (5), allocation read probes (4), Unix permissions (4), and transport validation
  (4). These are retained failures, not a local all-green claim.
- Complete RuntimeAssembly integration class: **105/105 passed**, including 58
  maintenance cases, real TLS, multiple sessions, compatible held proxies,
  stale/replayed controls, partial commit/disconnect, authentication expiry,
  collectible lifetimes, retirement cleanup and configuration rejection.
- The final producer correction retains one existing selection reservation through
  the complete outgoing group and disposal. All 60 producer ownership controls
  and three scoped registration-witness controls pass within the full unit suite.
  The same RuntimeAssembly run includes the held-disposer/old-module negative
  boundary and the intentionally supported immutable unary-serializer case.
  Extension fault-containment integration passes **20/20** with both forced
  reply/disposal terminal orders.
- The 18 absolute Stop-budget controls all pass, including two deterministic
  source negatives that failed before the correction. The unchanged active and
  retired callback Stop cases retain their five-second join watchdogs.
- Final no-ref cancellation-tail controls: **19/19 passed**, including pre-schedule
  cleanup and reentrant release; these are not simulated `Task.Run` allocation
  failure tests.
- Transport shutdown correction: **12/12 new** and **30/30 existing lifecycle**
  cases passed on both runtimes. The identical new fixtures on the known-negative
  source yield eight failures and four positive controls.
- Eight shipping package APIs and the exact SDK forwarding inventory match the
  reviewed baselines. Public additions are 64 lines across Abstractions/Client/Server.
- Prefix guards, project-reference boundaries, maintainability guards and their
  Python controls pass. Hosted canonical formatting and platform checks remain
  authoritative where local named-pipe access prevents the solution formatter.

The three historical full-suite regression runners retain exact equality and all
focused/old-negative controls. Their current combined inventories are 2,412 and
2,420 for the runner that overlays eight lease-race cases. The explicit fixture lifecycle bound changes below are separate from
unchanged callback watchdogs and performance/allocation thresholds.

## Ordinary cost and performance boundary

See [the measured cost report](cooperative-maintenance-cost.md) and the portable
[enabled TLS/native](../eng/maintenance-hot-cost/) and
[matched streaming](../eng/maintenance-stream-cost/) probes.

No catalog/version/update query or counter is added to audited ordinary RPC
selection/dispatch. That does **not** mean unchanged machine work: the healthy
finite-deadline progress helper removes a 40-byte closure while its accelerated-PGO
reservation changes 136 to 168 bytes (wrapper 120 to 216), with a surviving map spill
and reload. The ordinary reader reserves another 16 bytes in two optimized modes;
phase validation has mixed initialization/codegen changes. Background scans and
exceptional cancellation ownership have separately disclosed costs. Neither the
structural criterion nor the allocation improvement proves throughput equivalence.
The real TLS sample ranges overlap and include an unfavorable runtime/mode median.
The final producer delta also changes ordinary Unary/Retry accelerated-PGO
frames **376→392 / 456→472 bytes**: existing release/CAS work is relocated, not
added as another decrement. Client-stream optimized entry adds one 64-byte zero
store; Tier0 reservation and zeroing also increase, and duplex has a smaller entry
delta. See the [source-bound producer report](../eng/producer-hot-cost/RESULTS.md).
Its 40 local native/functional launches do not measure elapsed throughput. Earlier
c242 timing samples are not claims about final8915. Performance acceptance beyond
these observations remains open.

## CI-directed fixture corrections and retained evidence

The first feature head's three queued-reader failures now have direct terminal
observations: every worker completed all 11,000 operations without an inner
callback failure, but exceeded the separate aggregate ten-second wait. The
[fixture lifecycle report](reader-dispatch-fixture-lifecycle.md) preserves those
red runs, distinguishes the still-unknown scheduling cause and older unobserved
#931 failure, and documents the explicit test-only whole-workload **10 to 30 second**
and method-parent **30 to 60 second** changes. The per-callback and join watchdogs
remain ten seconds, with unchanged workload, assertions and concurrency. Two new
real controls distinguish delayed progress from a genuinely stalled callback.
This is not a production-performance threshold change or a green-retry explanation.
The separately joined SCC fixture tail closes test ownership without a causal
claim about the observed scheduling delay.

## Hosted acceptance

The feature workflow preserves the canonical Fast/Extended/allocation/API/codec
jobs and adds source-prefix guards, actual x64/Arm64 allocation-oracle controls,
and source-bound enabled TLS/native/streaming evidence. A reusable producer job
adds fresh paired c603/c242 versus exact-head/3bf0 evidence on both official
runtimes; the existing canonical job bodies are unchanged. The existing optional
browser/WASM matrix jobs remain explicitly skipped, not covered. All exact-head
runs, failures and fresh artifacts are linked from the draft PR after completion.
A pending workflow is not a successful check.

Neither prior research archives nor the excluded queued-handoff fixture are
uploaded or executed by this workflow. Only ordinary reviewed source and fresh
outputs from the published validation jobs are part of this deliverable.

### Source reconstruction setup correction

The first feature run's two new evidence jobs stopped before build or measurement:
`git apply` discovered the enclosing checkout while running inside an archived
source arm and silently skipped three patch files. The existing complete source
hash guard correctly rejected the result. The corrected scripts isolate Git's
repository discovery and inherited routing variables for that apply operation;
all expected prerequisite source hashes are unchanged. Five source-only regressions
cover the original failure, nested successful reconstruction, inherited routing,
unchanged outer files and deliberate identity mismatch. Failure provenance is now
written before rejection so the always-upload step can retain it. Neither setup
run is counted as a runtime/native/performance result.
