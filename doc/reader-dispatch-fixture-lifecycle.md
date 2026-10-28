# Queued reader fixture: correctness waits versus workload lifetime

This is a test-only correction. No `ReadOwnershipPipeReader`, RPC runtime,
ThreadPool configuration, suite parallelism or production timeout changes.

## What the failures establish

The first #933 source head was
`5a74fa34877f79ab685e6c55c181ed2ef88efbb1`. Its
[snapshot run](https://github.com/SunSi12138/SharpLink/actions/runs/39740346859)
and [lease-overlay run](https://github.com/SunSi12138/SharpLink/actions/runs/39740346998)
recorded three failures in
`QueuedPublicationOrLateRegistrationShouldDispatchExactlyOnce`. The failed
assertion was the outer ten-second wait for the **whole 11,000-operation workload**,
not the individual callback's ten-second lost-wakeup watchdog.

Fresh retained diagnostics show that all three dedicated workers subsequently
joined successfully, completing exactly 11,000 iterations, callbacks and
`Advance` operations with no worker error. At the outer timeout they had completed
10,601, 9,049 and 10,654 iterations. Their largest individual callback waits were
about 1.124, 1.714 and 1.714 seconds. Thus these three runs demonstrate an aggregate
duration overrun, not a lost callback or duplicate dispatch. They do **not** prove
which competing workload or scheduler condition caused the delay.

Raw reports are retained in the
[snapshot artifact](https://github.com/SunSi12138/SharpLink/actions/runs/39740346859/artifacts/12409485150)
and [lease artifact](https://github.com/SunSi12138/SharpLink/actions/runs/39740346998/artifacts/12408684997).
The earlier #931 ten-second failure had no terminal worker diagnostics, so its
cause remains separately unestablished. Its original red run and bounded
unchanged-budget investigation remain linked in
[the original report](https://github.com/SunSi12138/SharpLink/pull/931#issuecomment-6334101442).

## Explicit test-bound change

This fixture tests exactly-once continuation and no-lost-wakeup behavior. It is not
a declared throughput benchmark. Reusing its per-callback watchdog for the entire
11,000-operation workload imposed an additional, undocumented aggregate throughput
constraint. The correction makes the bounds distinct:

- Per-callback wait: **10 seconds, unchanged**.
- Dedicated worker join: **10 seconds, unchanged**.
- Whole-workload wait: **10 to 30 seconds**.
- Method-only parent lifecycle ceiling: **30 to 60 seconds**. The assembly-wide
  default is not changed. This covers the 30-second workload, the original
  10-second join, bounded negative-control cleanup, and test-framework diagnostics.
- Work remains exactly 11,000 iterations, with every
  original callback/advance count and result assertion, and concurrent publication
  variants retained. No tests are serialized and no ThreadPool minimum is raised.

The two new controls distinguish these contracts. A genuine 11,000-operation
workload pauses twice for six seconds on its dedicated worker **between completed
iterations**: it may exceed ten seconds overall without stalling any callback.
A separate control withholds the first actual publication and must fail the
original inner ten-second lost-wakeup check, not wait for the thirty-second
aggregate ceiling. Its physical callback is released and drained before event
ownership ends; failed bounded cleanup transfers event disposal to the actual
callback-end witness instead of disposing under live code.

These are correctness/lifecycle bounds, not a relaxation of any canonical
allocation or performance regression threshold. Both changed ceilings are
intentional and disclosed; it would be false to describe all timeouts as unchanged.
Exact final test inventories and hosted results are recorded by the PR's final
source-bound validation, not inferred from a green retry.

The sealed reader fixture independently passed all 16 focused cases on official
runtime 10.0.13. In that replay, the delayed-progress control completed all 11,000
operations in about 12.109 seconds; the stalled control observed the original
inner watchdog failure and one physically drained cleanup callback. This is a
control of fixture semantics, not a reproduction of hosted scheduling pressure.

### Timer-clock oracle follow-up

The first version of the new stalled control additionally required a high-resolution
`Stopwatch` measurement to be at least exactly 10.000 seconds. Five later jobs
failed only that new assertion, with measured waits of 9.999249 to 9.999980 seconds:
[main snapshot](https://github.com/SunSi12138/SharpLink/actions/runs/39745329573),
[main lease](https://github.com/SunSi12138/SharpLink/actions/runs/39745329554),
[main terminal](https://github.com/SunSi12138/SharpLink/actions/runs/39745329551),
[physical lease](https://github.com/SunSi12138/SharpLink/actions/runs/39745339723),
and [physical Fast](https://github.com/SunSi12138/SharpLink/actions/runs/39745637794).
Every case preserved the original inner lost-wakeup exception, joined its worker,
and physically drained exactly one cleanup callback and `Advance`.

The [.NET 10.0.13 implementation](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/System/Threading/ManualResetEventSlim.cs#L402)
converts the timeout to integer milliseconds and uses `Environment.TickCount64`
with [remaining-time accounting](https://github.com/dotnet/runtime/blob/v10.0.13/src/libraries/System.Private.CoreLib/src/System/Threading/TimeoutHelper.cs#L19)
before `Monitor.Wait`. This does not support the added cross-clock minimum-precision
assertion, nor does it identify the exact native timing sequence in those runs.
The correction instead records and asserts the actual ten-second argument passed
to the inner wait, retaining the original error-identity, physical-drain and join
checks. Elapsed time remains diagnostic; no epsilon or timeout adjustment is used.
This follow-up changes none of the configured bounds listed above.

## Independent fixture ownership correction

A source/timeline audit covered 144 relevant physical-work, generated-mapper,
cancellation, transport and local-abort cases that passed in each failing full
run. It found no intentional blocking worker or live timer surviving those
successful paths. Callback lifecycle controls started after both lease-arm outer
timeouts, so they cannot explain those two failures; they did overlap the separate
snapshot case. This is not a claim of universal cleanup after arbitrary test
failure or proof of a particular scheduling cause.

One SCC fixture released an unrelated plain-CTS owner's hold without awaiting its
nonblocking finalization. Its `finally` now awaits that existing completion under
the unchanged five-second watchdog. This closes a real test-owned tail, but is
**not** presented as the cause or fix of the observed ten-second aggregate delays.
