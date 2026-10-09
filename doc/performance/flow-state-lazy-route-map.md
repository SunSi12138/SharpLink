# #735 second footprint reduction: lazy non-default route dictionary

## What changes

A request that uses only the default stream-0 route already has a dedicated
`_defaultDispatcher` slot. It does not need the separate nonzero-stream dictionary.
This increment leaves that dictionary absent until the first nonzero stream is
published under the existing request-registry lock. Missing-route queries and
cleanup do not allocate it. Once materialized, the dictionary uses the same
identity, locking and removal semantics as before.

The complete receive lease, registration/bind rollback, acquired-DATA lifetime,
terminal ordering, cleanup pin and pooled-dispatcher safety are unchanged. This
is not a second state map, an entry pool, a smaller negotiated capacity or a
change to the protocol. It does not modify `StreamFlowController` or the
first-DATA resolve/debit sequence.

## Control, candidate and measured allocation

Control: `2a649a9880589696448e21ce0bd2e67ffd174147`, already containing compact
lifecycle flags. Original routing blob: `4920e4c6b1d2f6c0647986c6662c969b04d4080c`.
Candidate routing blob: `a8fa8e890ceaf341a552651fc289c3bddc0b1c4a`.
Test blob: `2d06e535fcc4a6b355ed6480179db72ba03f5418`.

The same previously committed route-allocation probe was used: one reused
stateless dispatcher, no flow callbacks, 2,048 warmup lifecycles followed by
three repeats of 10,000 synchronous Register/Unregister lifecycles. Control and
candidate were built/measured on the same Linux x64 runner. File writes and
formatting are outside the allocation measurement.

| Single-route request | Control B/route, three repeats | Lazy map B/route, three repeats | Reduction |
|---|---|---|---:|
| default stream 0 | 240 / 240 / 240 | 160 / 160 / 160 | **80 B/route** |
| nonzero stream 7 | 376 / 376 / 376 | 376 / 376 / 376 | **0 B/route** |

The gate required a real default-only reduction and no increase for the nonzero
control, in every repeat. Nonzero and mixed requests still allocate their needed
map; the 80-byte saving is not claimed for those requests.

The previous, separately retained flag-packing comparison was 256 -> 240 B/route
for stream 0 and 392 -> 376 for stream 7. The chained controls therefore show
256 -> 240 -> 160 for default-only lifecycles and 392 -> 376 -> 376 for named
lifecycles. This is **96 B less default-route lifecycle allocation** relative to
the pre-optimization 6ca43ee8 probe, and 16 B less for the named control.

These are allocation-attribution measurements, not actual RPC B/item or throughput
percentages. They do not prove that every stream shape meets the dev baseline,
and do not establish NativeAOT short-stream performance acceptance.

## Pre-submit validation

Local execution remained unavailable. The existing temporary preflight branch
ran a fresh, read-only validation before this source was promoted to the PR:

- Preflight head: `b2668d2b03c6b61f38e7b71c045a554ebebd32d4`.
- Run: [37335422646](https://github.com/SunSi12138/SharpLink/actions/runs/37335422646), attempt 1.
- Job: `111848850430`.
- Artifact: `11355583391`, 29 files, including raw allocation CSVs, source diff,
  the intended control failure, build/full-test logs and native self-check.
- Actions-reported artifact SHA256:
  `5f546e0994b85e4997e76b48513dd69f98f9a49a31110402cb0d4156b31ced63`.

The checkout first verifies that shipping `src/` is identical to the published
control. `DefaultOnlyRouteDoesNotAllocateNamedDictionary` then fails against that
unchanged control with the exact intended assertion. After the source-pinned
transformation, all six new cases pass. The 11 packed-flag/allocation cases also
pass before and after the dictionary change.

First review round:

- Release builds: zero warnings/errors, with warnings treated as errors for
  UnitTests and the JIT writer build.
- Changed-file whitespace validation: passed.
- Original maintainability gate: passed, no allowance increases.
- Production reference boundary: passed, 9 projects and 13 active references.

Second review round:

- New dictionary cases: **6/6**.
- Retained flag/allocation cases: **11/11**.
- Complete default UnitTests: **1978/1978**, zero failures/skips.
- Complete experimental UnitTests: **1978/1978**, zero failures/skips.
- Actual JIT writer self-check: **171/171**.
- NativeAOT publish and executable writer self-check: **171/171**.
- Existing writer Python guard suites: 17 + 12 + 8 + 13 tests passed.

New coverage checks absent-map behavior, default/nonzero identity independence,
missing-nonzero operations, request/connection retirement, and concurrent publication
of default and named routes. All earlier acquired-credit, ABA, bind rollback,
reference-retention and cleanup-pin tests remain in the full suites.

The temporary workflow and transformation are not added to the original PR.
No production acceptance threshold, workflow gate, timeout, flow window or test
population is relaxed. Native publication/self-check success here must not be
presented as fixing native short-stream latency.

## Remaining acceptance

The PR remains Draft. Read the new production E2E/allocation and NativeAOT reports
for the combined head rather than subtracting lifecycle bytes from older E2E
numbers. Compact route lease storage, first-DATA resolve/debit overhead and
connection contention are separate investigations; these two footprint changes
do not silently claim to solve them.
