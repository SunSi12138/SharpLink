# Cooperative maintenance: validation and acceptance boundary

This is a draft production candidate for the deliberately restricted capability in
[ADR 0004](adr/0004-cooperative-session-maintenance.md), not completion of every
requirement in #775. It is not a zero-overhead or throughput-equivalence claim.

## Composition

The PR targets #928 (`37b92f20e20ce8a7b751660d56d636a0ce1660e7`) without merging it.
It includes the physical-work prerequisite from #931 (final reviewed branch
`ec8f268b87757e16b74839dd2a6d453348cd476d`) and intrinsic-send prerequisite from #932
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
and twenty source-region pins are conservative review guards, not an execution of
the excluded queued-handoff fixture or a formal proof of arbitrary future changes.

## Final local source-bound checks

Production subtree: `c242f33f0041439b176c43460864a888237b79d9`.
Build SDK 10.0.102, Release, zero warnings/errors. Tests below ran with official
runtime 10.0.13, one reported processor and TUnit parallelism eight.

- Full unit suite: **2,329 executed, 2,301 passed, 28 failed**, no skipped,
  cancelled or timed-out tests. The exact 28 case identities match the previous
  isolated prerequisite/environment run: 26 Unix socket/named-pipe permission
  denials and two expected-exception assertions receiving that socket denial.
  Categories are shared-memory control (7), pipeline (4), named-pipe read ownership
  (5), allocation read probes (4), Unix permissions (4), and transport validation
  (4). These are retained failures, not a local all-green claim.
- Complete RuntimeAssembly integration class: **104/104 passed**, including 57
  maintenance cases, real TLS, multiple sessions, compatible held proxies,
  stale/replayed controls, partial commit/disconnect, authentication expiry,
  collectible lifetimes, retirement cleanup and configuration rejection.
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
focused/old-negative controls. Their current combined inventories are 2,329 and
2,337 for the runner that overlays eight lease-race cases; no threshold is relaxed.

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
Performance acceptance beyond these observations remains open.

## Preserved validation risk

The #931 precursor had one aggregate 10-second timeout in an unchanged 11,000-read
queued-publication fixture. Its original worker outcome was not recorded. One
bounded diagnostic campaign retained all limits/assertions and passed four full
runs and four focused runs; all sixteen recorded workers finished exactly 11,000
operations. That does **not** establish the original cause or a fix. Minimal durable
progress/terminal diagnostics are retained, and the final #931 exact-head checks
are green. Treat this as a disclosed timing/concurrency validation risk, not as a
proven feature defect or a proven harmless baseline flake. See
[the original evidence and bounded diagnosis](https://github.com/SunSi12138/SharpLink/pull/931#issuecomment-6334101442).

## Hosted acceptance

The feature workflow preserves the canonical Fast/Extended/allocation/API/codec
jobs and adds source-prefix guards, actual x64/Arm64 allocation-oracle controls,
and source-bound enabled TLS/native/streaming evidence. The existing optional
browser/WASM matrix jobs remain explicitly skipped, not covered. All exact-head
runs, failures and fresh artifacts are linked from the draft PR after completion.
A pending workflow is not a successful check.

Neither prior research archives nor the excluded queued-handoff fixture are
uploaded or executed by this workflow. Only ordinary reviewed source and fresh
outputs from the published validation jobs are part of this deliverable.
