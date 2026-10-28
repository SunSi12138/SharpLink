# Reader allocation oracle: controlled runtime steady state

The two exact-zero reader tests now measure a deliberately warmed runtime cast cache,
not arbitrary activity in the full test-runner process. Production reader code is
unchanged. The measurement loops still execute once, including their
1,000 warmup rounds, 10,000 measured rounds, distinct consumer paths, and zero-byte
assertions. No allocation is subtracted or exempted.

The dev integration also preserves the Gen2-disturbance guard introduced by #935:
the child completes a forced collection before warmup and rejects any Gen2
collection during the warmup/measurement interval. This is independent of the
stable cast-cache precondition. Neither a disturbed GC interval nor a replaced
cast-cache table is accepted as a zero-allocation pass. The additional guard does
not change the 1,000 warmup rounds, 10,000 measured rounds, or exact zero budget.

## Why process isolation alone was insufficient

An unchanged baseline and candidate both reproduce runtime `Int32[]` allocations
on the reader stack when another thread unloads collectible assemblies. CoreCLR
flushes its process-wide cast cache on unload. A 1,024-entry table occupies exactly
24,624 bytes on the verified 64-bit runtimes. The natural-operation witness uses
ordinary casts and assembly unloads, with private runtime reflection used only to
read the current table. It does not inject or modify runtime state.

A filtered TUnit child subsequently failed with 98,352 bytes. A standalone child,
without running TUnit discovery or its entry point, still occasionally failed with
6,192 bytes. Read-only table observations showed growth from 128 to 256 entries,
with no GC during the method. Native allocation/unload callbacks confirmed the
`Int32[]` reader stack and no assembly unload in those standalone failures. These
negative results are retained; neither process isolation alone nor disabled
tiering is presented as the repair.

The original historical full-suite failure was not captured by the profiler.
The witnesses establish the same allocation mechanism in unchanged baseline and
candidate code, not the identity of a particular historical unloading test.

## Explicit precondition and failure behavior

`SharpLink.ReadOwnershipAllocationProbe` is a test-only console project. It loads
the parent's exact UnitTests module, validates its module ID, and binds one typed
delegate to the requested original measurement. It never invokes the test runner.
An ordinary non-default `AsyncLocal` context is established before warmup and
checked afterward; context flow and default tiering remain enabled.

Before invoking that delegate, the host creates noncollectible types and performs
ordinary `IFormattable.IsInstanceOfType` checks. These naturally grow the shared
runtime cast cache. The bounded precondition requires a 4,096-entry table within
20,000 types and 15 seconds. Private reflection only reads the table and validates
its array length, power-of-two mask and 64-bit hash-shift header. The identical
table object must remain installed across the single original measurement.

The supported capability contract is dynamic-code CoreCLR 10 on x64 or arm64 with
the observed layout. Unsupported runtimes/layouts fail explicitly as unavailable
test infrastructure, never as a skipped or zero-allocation pass. The 4,096-entry
maximum is source-verified for 10.0.2 and 10.0.13. Other servicing builds are
capability-checked, not historically validated; a universal future maximum is not
assumed. Any measured allocation or table replacement still fails.

The parent requires child exit zero, exactly one nonce/case/module/runtime/actual
process-architecture-bound success marker, and matching JSON proving exactly one
completed measurement. A compact validated JSON record is retained in ordinary
test output after those assertions, including the child's actual architecture.
The original assembly-wide execution timeout was 30 seconds. The entire child,
including startup and priming, retains that 30-second limit. The 90-second outer
test ceiling reserves process launch, cleanup and reporting time, not a larger
measurement or allocation budget. A live timed-out child is killed and joined
within bounded cleanup. Failed
child output is retained. There is no retry-until-zero, extra reader warmup,
GC suppression, assertion relaxation, or full-suite serialization.

Two additional in-process semantic cases retain real concurrent collectible
assembly unloads for both consumer styles. Each verifies actual unload, suspended
reads, exact-once consumption/release, and continued progress for at least 11,000
reads. They are not allocation oracles. Test inventory increases by two.

## Local evidence and limits

- Before the final capability-guard wording, 11 lifetime repetitions per runtime
  passed 37/37 cases on x64 10.0.2 and 10.0.13: 814 case executions.
- Guarded dotnet and apphost launch routes each passed 37/37 per runtime. Four
  unaffected reader groups passed 25/25, 5/5, 14/14 and 12/12 per runtime.
- The final capability/header guard also passed 37/37 lifetime cases on each
  verified runtime build before sealing.
- An independent ordinary-cast precondition harness passed 80/80 combinations of
  unchanged baseline/candidate, both runtime builds, and both original bodies.
  It retained identical table identity throughout each method, with no GC there.
- A real negative control inserted `GC.KeepAlive(new byte[256])` inside the measured
  interval in a separate, unpublished diagnostic checkout. Both runtime builds
  failed the original assertion with exactly 280 bytes; child exit 1 propagated
  as parent test failure. The allocation-injecting edit is not part of this change.
- Profiling the proposed host placed cache-growth allocations in startup and the
  ordinary-cast precondition, not in the measurement, with no assembly unload.
- Local testing is x64. Hosted arm64 and integrated full-suite acceptance are
  separate required validation; these local results do not claim either.

At the historical #931 source checkpoint, against public baseline
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`, raw measurement bodies
are byte-identical after moving them into private static methods. SHA-256 below
is over UTF-8 from the opening `{` through the closing `}`, inclusive, without
surrounding indentation/newline; all whitespace inside the body is included:

- Successful: `2de020f10445d0fabbc4a065ce60c348bd94128fcceab774d9e97d74383f6cd8`
- Registered: `f8e87a1ff152b1baed738db50e70ec0d0870098703d93948e20bf94989b8329c`

These are historical hashes, not hashes of the integrated methods containing the
additional #935 Gen2 guard. Integration validation must run both consumer styles
and the real 280-byte counterfactual against the combined source; the prior hashes
or prior green jobs cannot substitute for that run.

## Primary runtime sources

The two versions' `castcache.h` and `castcache.cpp` are byte-identical. They define
the 64-bit entry layout, release capacity, growth/replacement and unload behavior:

- [10.0.2 cache definition](https://github.com/dotnet/runtime/blob/v10.0.2/src/coreclr/vm/castcache.h)
- [10.0.2 cache allocation](https://github.com/dotnet/runtime/blob/v10.0.2/src/coreclr/vm/castcache.cpp)
- [10.0.13 cache definition](https://github.com/dotnet/runtime/blob/v10.0.13/src/coreclr/vm/castcache.h)
- [10.0.13 cache allocation](https://github.com/dotnet/runtime/blob/v10.0.13/src/coreclr/vm/castcache.cpp)
