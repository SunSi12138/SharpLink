# #735 first footprint reduction: compact route lifecycle flags

## Scope and acceptance boundary

This increment reduces per-route metadata storage. It does not resolve the
remaining production short-stream allocation, NativeAOT short-stream latency,
or connection-wide contention acceptance concerns. Keep #742 Draft and #735 open.
No existing workflow gate, timeout, allocation budget, protocol field, flow-control
window, receive lease, pool policy, or acquired-DATA retirement rule is changed.

The control runtime is `6ca43ee8e434f2796a9e8d65f49d777d36f52fd2` (which includes
dev `0fe26024b114bb6e78411a9b86276086c045d03d`). The only production-file change
is in `StreamManager.Routing.cs`: three cold one-shot lifecycle flags move from
separate integer fields into spare bits 34, 35 and 36 of the existing atomic
entry state word. The existing acquisition count, Close, Detach and receive-cleanup
pin keep their original disjoint bit ranges and transitions.

Terminal publication and retirement still have exactly one winner. Peer-terminal
publication uses atomic OR so concurrent acquisitions cannot overwrite it. The
complete generation-bound receive lease remains independently owned by the route;
this change does not read it back from a dispatcher that may be clearing or pooling.

## Pre-submit execution, not a local-build claim

The local executor was unavailable. Validation therefore ran on a dedicated
read-only GitHub Actions preflight branch, without modifying the original PR branch
or creating another PR. The workflow first verified every shipping `src/` file
matched the fixed control, built/measured that control, then applied only the
blob-pinned flag transformation in its disposable checkout and repeated the tests.
The temporary preflight workflow and transformation are not part of this PR increment.

- Preflight head: `02a12f10dfc1dee03c280da9da5d2b5391f42d36`.
- Successful run: [37331620087](https://github.com/SunSi12138/SharpLink/actions/runs/37331620087), attempt 1.
- Job: `111835863430`.
- Artifact: `11355220166`, 27 retained files.
- Artifact SHA256 reported by Actions: `6108ec4ee955ef185769fbc4c4f90f45b7e07a3e0fa23382296fc3de64898074`.
- Original routing blob: `d5c9c8d7beff4ea53feb3f2c384a80f184364cd5`.
- Candidate routing blob: `4920e4c6b1d2f6c0647986c6662c969b04d4080c`.
- Test blob: `5c4f0ba7a5eac64b30c31a30d4caf5bf0a3b78cf`.

The first preflight run `37331359076` stopped before compilation because its shallow
checkout could not resolve `HEAD^`. Its failure artifact is retained. The fix fetched
history and checked the fixed control's ancestry plus an empty shipping-source diff;
no runtime change, test assertion, or measurement threshold was relaxed.

## Measured allocation result

A single reused stateless dispatcher, no flow callbacks, 2,048 warmup lifecycles,
then three repeats of 10,000 synchronous Register/Unregister lifecycles per mode.
Control and candidate run sequentially on the same Linux x64 runner. Allocation
is measured with `GC.GetAllocatedBytesForCurrentThread` around the lifecycle loop;
output formatting and file writes occur afterwards.

| Route | Control B/route, three repeats | Packed flags B/route, three repeats | Reduction |
|---|---|---|---:|
| stream 0 | 256 / 256 / 256 | 240 / 240 / 240 | **16 B/route** |
| stream 7 | 392 / 392 / 392 | 376 / 376 / 376 | **16 B/route** |

The comparison required at least 16 B/route reduction for both routes in every
repeat. It is a lifecycle-allocation attribution control, not a production RPC
throughput result, not a native allocation measurement, and not proof that total
B/item has returned to the dev baseline. The recorded nanoseconds are not used as
a speed acceptance claim: this small sequential allocation probe is not a complete
AB/BA timing experiment.

## Two review rounds before promotion to the original branch

Engineering/build:

- Control and candidate UnitTests Release builds: 0 warnings, 0 errors, with
  `TreatWarningsAsErrors=true`.
- Writer and experimental UnitTests Release builds: 0 warnings, 0 errors.
- Changed-file formatting: passed.
- Production reference boundary: passed, 9 projects and 13 active references.
- Original maintainability check: passed, no allowance increases.

Contract/regression and runtime checks:

- New focused cases: **11/11** on both the unchanged control and packed candidate.
- Full default UnitTests: **1972/1972**, zero failures/skips.
- Full experimental UnitTests: **1972/1972**, zero failures/skips.
- JIT actual-writer self-check: **171/171**.
- NativeAOT publication and actual-writer self-check: **171/171**.
- Existing writer evidence-guard Python suites: 17 + 12 + 8 + 13 tests passed.

The 11 new cases in `StreamManagerPackedFlagsTests` include all six ordering
permutations of terminal/retirement/peer flags, concurrent single-winner publication
while acquisition counts change, count saturation at the original limit, cleanup-pin
retirement, and both allocation probes. They protect semantics rather than asserting
a private field layout. They deliberately pass on the control too; the separate
allocation comparison demonstrates the footprint reduction.

The complete suites retain all previously added same-key ABA, acquired receive-credit,
registration rollback and pooled-reference retention regressions. No test was removed
or skipped. NativeAOT success here is a compile/correctness result, not a claim that
the NativeAOT short-stream performance regression is fixed.

## Remaining work

Measure this source under the original PR's ordinary CI and production evidence
workflows. Continue reducing duplicated per-route lease storage only while preserving
immutable generation ownership and callback lifetime. Independently investigate
first-DATA resolve/debit fusion and the remaining contention cost. Each change needs
its own attribution and production E2E evidence; a 16-byte metadata reduction is not
a substitute for #735's joint B/item and NativeAOT requirements.
