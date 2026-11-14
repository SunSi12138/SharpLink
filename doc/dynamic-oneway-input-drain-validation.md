# Dynamic OneWay input lifetime: frozen validation

Base: dev 0acc813a1a520a0d7e2b3b8e66ef8f8a535575ca. Server/Runtime source equals prior a2639c44 audit.
The first subject changes fixtures/tests only. No product fix may be adopted until the public generated path reproduces the defect.

## Baseline gate
- Public RegisterAssembly; genuine generated contract/Stub and custom typed Codec in collectible plugin.
- Singleton and Call service lifetimes; OneWay/count1 subject, ClientStreaming/count1 positive control, OneWay/count0 nonstream control.
- Codec executes on real TCP StreamData and is held by an explicit release gate (20-second failure watchdog).
- Handler has completed while decode remains active. Unregister uses its existing two-second graceful policy.
- Exact defect marker: DYNAMIC_ONEWAY_PHYSICAL_EXIT_V1 only when ReferencesReleased=true, serviceDisposed>0 and activeDecodes=1.
- Positive controls must preserve module protection; count0 owns one call and no stream. No sleeps, weakened cleanup or transport hooks.
- Release every fixture gate in finally. A fixture/build/watchdog failure is not the defect marker and stops adoption until diagnosed.

## Candidate gate, if baseline proves the defect
Retain the original tests and exact marker. Fix only dynamic classification and existing request dispatch drain.
Require singleton/call, count0, input positive controls; extend cancellation/exception/physical Dispose and active-runtime ALC/credit/route evidence on the actual candidate.
No Bind/receipt/API6/wire change or new per-call owner. Optional SPI defaults remain legal.
No QPS benefit claimed. Static/ordinary no-input paths must retain source semantics; targeted hosted allocation/cost checks and existing allocation budget apply.
All RPC/ALC execution and performance are GitHub temporary Actions only; local is build/non-RPC components.
Baseline/candidate evidence must identify production and fixture commits. Retain failures; no unchanged reruns to green.
#387 investigation remains paused. Final PR checks do not reclassify that historical failure.

## Acceptance
Exact baseline defect + current candidate original controls/necessary adjacent controls + targeted no new per-call allocation/cost evidence + exact-head CI.
Independent reviewer and parent own merge. This is not full #775 acceptance.

## First baseline subject
60a03285 / run43611586246: both generated OneWay subjects reached the exact physical-exit defect marker.
Both ClientStreaming subjects preserved the expected active protection but the fixture incorrectly re-read
the already-completed timed-out Unregister operation expecting its immutable result to change. Count0 passed.
Correct the post-release oracle to wait for the existing module Released state; do not change ReferencesReleased.
The report collector also needs to locate JUnit beneath the results directory rather than assume a flat path.
No product changed. Original failures and raw logs remain evidence; this run is not the full baseline gate pass.

The corrected test-only9ab3aea8/run43611786361 has precisely two original OneWay marker failures and four passing controls.
The wrapper still failed only because no JUnit exporter was installed. Reuse its actual console/HTML inventory;
do not rerun unchanged RPC to repair a collector. Candidate uses exact summaries plus twelve source-bound case JSONs and real HTML reports.

## Candidate inventory frozen before execution
Keep the original six controls and add six OneWay singleton/call terminal cases: throw, cancel and compatible service Replace.
Each case must collect the retired plugin within ten full GC/finalizer attempts while client/server remain active.
Five separate non-RPC component cases cover one/two input gates, captured-child teardown, held physical asynchronous disposal,
late scalar discard credit/peer terminal and zero allocations in65536 warmed single-stream captures.
Both Linux and Windows run the identical frozen12+5 inventory. No timing speedup is claimed.
Capture is a value snapshot, inline for one input; only multiple inputs require a bounded reference array.
Ordinary Unary/non-input OneWay retains its original completion owner; no new per-call heap owner is introduced there.

