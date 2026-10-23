# Queued registration coherence: scope and cost ledger

## Correctness contract

An asynchronous admission continuation must not look up a replacement service
while retaining the previous registration's cancellation and stream decisions.
The request resumes the originally selected registration or fails closed if that
registration has retired or been collected. Fresh arrivals can still select a
compatible replacement; held client proxies are not given fixed-registration
identity by this patch.

Dynamic queued selection is weak, not a module lease. The registry owns the
registration until retirement; a held queue does not prevent old collectible
service code from being collected. The final existing module lease still decides
whether user code may enter. Static selection remains strong. Retired dynamic
registrations report Unavailable, while manually disposed running/static
registrations retain ObjectDisposedException's type, ObjectName and default
message. Existing cancellation, compression, retained-payload and stream-credit
cleanup rules are preserved.

Immediate dispatch signatures and request-loop ownership remain unchanged.
Queued continuation dispatch is deliberately outlined separately, with exact
source-parity guards against the immediate resource skeleton. No new normal-call
registration lookup, weak dereference, update epoch, version or counter is added.
That source statement is not a promise that the JIT generates identical code.

## Measured cost, including rejected representations

Pinned baseline: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`.
SDK10.0.102; separate paired runs use runtimes10.0.2 and10.0.13 on Linux x64.

- The initial generic-selection representation grew ordinary OneWay stack frames
  and prologue zeroing. It was not accepted as the no-added-hot-work route.
- Outlining immediate/admitted dispatch restored ordinary signatures and measured
  state-machine sizes. RPC waiter192 B, OneWay waiter200 B and request dispatcher
  104 B match baseline. The retained admission payload grows56→72 B.
- ServiceRegistration grows96→104 B, including static registrations with an empty
  slot. Each dynamic registration allocates one24 B managed WeakReference plus
  its native GC handle during registration. Queued attempts reuse that immutable
  identity, rather than allocating a new weak handle per attempt.
- Combined cold capture measured FullOpts RPC7003→7009 native bytes. OneWay is
  7699→7706 on10.0.2 and7696→7703 on10.0.13. Both keep baseline stack reservations
  (0x528/0x538); the shared request caller remains3245 bytes/frame312.
- Tier0, instrumented Tier0 and non-PGO Tier1 frame reservations match in both
  runtimes. Native bytes differ around the cold-copy call and exceptional throw
  classification. Existing field displacements change with registration layout.
- **Default PGO is not invariant.** Of four paired candidate runs on each runtime,
  three RPC frames were0x7A8 and one0x7B8; all four baseline frames were0x7A8.
  The larger candidate includes an ordinary cancellation-token spill/reload,
  not merely an unused stack reservation. Code/profile variation also occurs
  between identical-source launches. All samples remain in the numeric results;
  this is not hidden by a nonsignificant timing claim or a relaxed budget.

Consequently the combined-capture representation is a correctness candidate with
an unresolved strict hot-cost acceptance point, not a proven zero-work update
architecture. Allocation/layout/native evidence is not throughput equivalence.
Further representation experiments must preserve the same behavior and report
both normal and saturated/queued paths rather than reclassify saturation as free.

## Verification ledger

On the separated source:25/25 ServiceRegistration cases,68/68 complete runtime
assembly integration cases,52/52 integration Queued selection,41/41 unit Queued
selection and exact paired source guards pass. A clean combined-copy rebuild
repeats25/25 and68/68. The three parity guards pass independently.

The composed delivery source also includes a test-only retry timer-registration
correction. Its full local official10.0.13 run is2137 total,2109 passed,28 failed,
zero skipped. The28 IPC/environment failures are retained, not called a passing
suite. Build reports zero warnings/errors. Hosted exact-head full CI remains the
acceptance authority for that environment; old-head success is not substituted.

The original queued cancellation counterexample used real TCP and failed on the
baseline. Additional controls cover retired/collected rejection, cancellation,
all request shapes, fresh replacement success, held-queue collectible collection,
and compressed payload with persistent decode ownership.

## Exclusions

This patch does not implement physical producer handoff closure, a FIFO session
fence, same-session update coordination, global physical quiescence or all of#775.
It does not promise prompt ALC collection in the presence of unrelated managed
roots. The separate capacity-rejection handoff change and its unexecuted dedicated
regression are not included in this PR.

See [portable commands and retained numeric results](../eng/queued-registration-probes/README.md).

### Further bounded outline experiments (local, not shipped)

Moving the existing compression decision into two specialized queued-copy helpers
removed the extra frame in four sampled PGO runs, but grew RPC Tier0 and instrumented
frames by16 B. Passing the sequence by readonly reference to the copy helper reduced
Tier0 native size, but enlarged optimized frames. Outlining the entire queued
copy/reservation/continuation branch, with the copy body inside that helper (no
extra queued wrapper call), reduced OneWay code/frame size and optimized RPC code;
its initial form grew RPC Tier0, while readonly ValueTask/sequence arguments reduced
Tier0 RPC6541→6234 bytes/frame0x7F0→0x7E0 but grew FullOpts RPC0x528→0x568 and sampled
PGO0x7A8→0x7E8. These mixed results are not throughput regressions or improvements
by themselves, and none establishes the strict unchanged-ordinary-work criterion.
Those prototype patches/listings are retained locally, not represented as shipped
production or publicly replayable evidence. The checked-in paired numeric results
belong only to the combined-capture source actually proposed here.
