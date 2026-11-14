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
The refined subject also waits until the mailbox lease-detach callback has returned, not merely until logical IsDetached is visible.
Only explicitly captured dynamic input invocations retain the disposal Task; unrelated legacy mailboxes keep observation-only cleanup.
The same12+5 controls now run Linux/Windows × TCP/SharedMemory. JSON paths are absolute to survive TUnit's working-directory change.
The prior9bb5b8c5/run43613254914 passed12+5 actual assertions on both hosts, including active-runtime ALC,
but collector JSON paths were relative and empty at the workspace root. Its wrapper failures remain; no full collection pass is claimed.
Before the refined run, freeze a separate allocation-only probe:32768 mailboxes after4096 warmups in each arm,
same host/runtime, identical reflection constructor driver; the retained Task+flag field delta must be0–16 B/mailbox.
This is bounded per-input-stream state, not a Unary allocation, an extra per-call owner object or a timing equivalence claim.

## Refined functional result
Production56bc05b5 / run43613725539: Linux/Windows × TCP/SharedMemory all pass the exact12 RPC/ALC +5 component inventories;
all48 source-bound case JSONs and12 actual HTML reports passed the collector. Both SharedMemory jobs succeed.
The two TCP jobs fail only afterward in the allocation tool's wrong BufferWriterPoolOptions namespace.
Correct that tool to Runtime; do not repeat the four valid functional subjects. Product source remains unchanged.
The subsequent temporary run performs only that allocation line plus the previously unrun ordinary512-case Integration
suite on Linux and Windows.512 is the actual discovered495 existing +12 RPC +5 component UID inventory.
The normal Unit census remains2212, so its fixed-old regression counts must not be edited.

## Final evidence and cost

- [Public baseline](https://github.com/SunSi12138/SharpLink/actions/runs/43611786361), test-only9ab3aea8 / production0acc813a:
  precisely Singleton/Call OneWay fail at DYNAMIC_ONEWAY_PHYSICAL_EXIT_V1; four controls pass.
  The actual console/HTML artifact14120800965 was read back offline. The wrapper remains failed for its missing JUnit exporter.
- [Refined functional subject56bc05b5](https://github.com/SunSi12138/SharpLink/actions/runs/43613725539):
  Windows/Linux × TCP/SharedMemory, each12 RPC/active-runtime ALC +5 physical-disposal/credit/capture components pass.
  The four downloaded ZIPs independently contain exactly48 source-bound JSON cases and12 actual HTML reports.
  TCP job failures are the subsequent allocation driver's namespace error, not a functional pass relabelled as a green workflow.
- [Completion subjectec6b8aad](https://github.com/SunSi12138/SharpLink/actions/runs/43614143833):
  all four jobs pass. Ordinary Integration is512/512 on Linux and Windows, zero failures/skips.
  Product source is identical to56bc05b5; only the collector/tool and test formatting changed.
- Allocation-only matched driver reports Linux +8.000 and Windows +7.811 B/mailbox relative to0acc813a,
  within the predeclared0–16 B state budget. The noninteger Windows delta includes common warmed reflection-driver work;
  do not interpret it as a fractional CLR field size. Both arms use runtime10.0.15,32768 constructions after4096 warmups.
  Single-input capture itself allocates0 bytes over65536 warmed captures. Multiple inputs use a bounded array.
  These results quantify the retained Task/flag state, not RPC throughput, tail latency or full#775 performance acceptance.
- No production public surface, generated API5 identity, wire2.4 grammar, OneWay ACK contract, optional Stub metadata fallback,
  descriptor representation or request/CTS owner is replaced. Existing static/non-input completion bodies remain intact.

The temporary workflow and validation branches are removed after consuming terminal runs; historical runs/artifacts remain.
Local downloaded ZIPs, offline exact-inventory readbacks, raw logs and discovery live under
artifacts/dynamic-oneway-drain/ in the owning worktree. No new raw log upload/release is part of this PR.

## Independent review: synchronous cleanup ordering gate

Review of9a4a9f17 found that "before the first await" was insufficient: Abandon of input1 can synchronously
discard buffered data and dispose its real pooled child, including receive-credit publication and public MeterListener
callbacks, while input2 still accepts business Codec work. The independent non-RPC Session probe reproduced this
exact gap. Its original report/probe/log remain in the separate SharpLink-984-Review worktree.

Before changing the product, add one permanent Unit case using the existing real Session fixture, generated inbound
children and public counting Codec/MeterListener. Hold input1's synchronous WindowUpdate-enqueue callback;
only after that callback enters, dispatch input2. Old9a4a9f17 must fail DYNAMIC_INPUT_CLOSE_ALL_BEFORE_CLEANUP
with firstCodecCalls1/secondCodecCalls1/drainCompleted=false. The repair must show secondCodecCalls0 while the
same cleanup callback remains held, then finish without peer terminal and preserve both scalar routes until terminal.
Release the gate and join Session cleanup in finally; a watchdog/build failure is not the defect marker.

Repair only the close-all boundary: close all captured child business-admission gates without invoking cleanup callbacks,
then initiate buffered discard/child disposal/detach callbacks and join. Retain the existing state fields, original ordinary
Abandon contract, credit routes and physical ownership. No global lock, owner object or new callback hook.

Local execution is limited to this non-RPC component; freeze its exact filter and retain old-head failure and candidate
result. Hosted verification covers this changed ordering and existing physical-disposal/scalar-credit components on
Linux/Windows, plus exact-head canonical CI. Reuse the four unaffected real-RPC/ALC quadrants and allocation evidence;
do not repeat them for reassurance. The new Unit UID requires an actual census increment2212→2213 (injected-control
runner2220→2221); fixed historical focused inventories remain unchanged.

The permanent component against unchanged9a4a9f17 products (test-only commitb4fd96b5) fails exactly once:
first1/second1/drainCompleted=false/routes2; its local console and HTML are retained. After the two-phase repair,
the identical filtered non-RPC component passes1/1. Phase one does not set detach-request/dispose state or
invoke external callbacks; it closes live/replay/completion/attachment admission using the existing flags. Phase two
performs the original Abandon cleanup after all siblings are closed. An independently already-running physical
operation may still finish concurrently and remains owned; phase one itself does not start its disposal/credit work.
No mailbox field/layout, snapshot allocation or ordinary non-input completion body changes in this correction;
the additional short mailbox lock occurs only on captured dynamic input completion. No timing equivalence is claimed.
Local discovery finds2218 total UIDs, including five existing Explicit process probes that ordinary execution excludes;
the ordinary canonical inventory is2213. Hosted current-full execution, not raw discovery alone, must confirm that count.
