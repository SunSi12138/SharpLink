# #775 client-local generations and dual physical owners

Base: `e866e623ee7db7a0b9e0fea1722232b25e8eb441`, the accepted stream-admission handoff.
Product dev remains `160d0cd9a0550de5bf00c63b6007376c02debc20`. This bounded stage enables
client-local runtime registrations on the existing nonshipping contract receiver.

## Keep the original wrapper boundaries

Reuse SharpLinkModuleRpcChannel, adapting only its inner receiver. Unary/OneWay acquire
before inner Codec/cancellation checks. ClientStreaming acquires distinct call and producer
leases before those checks. ServerStreaming/Duplex capture interceptor policy and the original
deadline at invocation; logical progress precedes module acquisition at first enumeration,
and Resolved Codec checks remain after acquisition. Expired progress can still precede a
retired-module error. Do not move validation into a generated factory or normalize these
families into the static inner-entry order.

Cold factories protect the exact registration while reading manifest/Codecs and constructing
the generated receiver. RpcLocalContract remains a client-issued scalar generation/hash;
it never resolves a replacement by ContractId alone. Retained registrations preserve their
generation across unrelated snapshot publications. Sites hold the noncollectible module
control block, not Type/Assembly/Codec. Ready Prepare and in-flight refresh both reject a
retired local generation, and retirement wakes Prepare waiters.

Replacement prepares fallible Codecs/registry resources first, closes the old local module
gate, then publishes the new proxy snapshot. New calls on old wrappers fail. Already
acquired work may keep its original scalar remote routes until physical drain; Session
invalidation still applies. Physical release clears those routes and the cold site entry.
No old local selector or BindingId is reinterpreted as a new local/remote registration.

## Both physical owners retain the original cancellation source

The original wrapper linked CTS is reference-counted across the call/consumer and independent
producer lease. Logical completion cancels pending production without disposing that CTS
while producer code still owns it. The existing pending producer CTS from the previous
stage also remains physically owned. Disposal of the last linked-source reference precedes
release of its final module lease.

Stream head work is independently queued. Retain the already acquired consumer owner before
queuing registration/serialization; this does not acquire a retired gate. For Duplex,
transfer the producer lease before queuing, so early consumer disposal cannot release an
unclaimed owner behind the queued task. Release the head's consumer retention after head
publication/failure, leaving the producer and consumer free to exit independently.
Existing dispatcher disposal joins active original response Codec dispatches before the
consumer wrapper releases its physical lease. Stop/timeout never reports physical reclaim
while one of these owners remains.

No new production public declaration or generated/wire signature is needed. Private Magic
0xBB, RequestAccepted244 and ABI `sharplink-experimental-775-stream-admission-v5` remain
unchanged. This remains a nonpackable development profile; ordinary product tokens/build
inputs must remain identical to the accepted baseline.

## Frozen acceptance and stop rules

All RPC/generated-runtime/lifecycle/GC execution uses a temporary GitHub Action, Windows/
Linux x64 × actual TCP/SharedMemory. Local work is development, basic builds and source/
archive checks only. No performance experiment is part of this stage.

Keep the exact 99 / group67 / receipt49 / revised facts49 / streams64 inventories unchanged,
including their 60/120/120/120/180-second watchdogs. Add an independent exact local-module
inventory before the first Action, with a 240-second process watchdog. Each synchronous
Codec cut has 15 seconds; normal replacement/unregister uses the original 30-second
grace on ManualTimeProvider, and the forced-drain control deliberately uses zero grace.
Each live-client/server ALC check is limited to the existing ten full-GC iterations.
The controlled Stop case is not used as evidence of live-client ALC collection.

Required outcomes for this bounded slice:

- Five wrapper guard/deferred boundaries; original invocation-time policy/deadline;
  local-only factories; no provisional lease leak; no silent local-generation retarget.
- Unrelated publication preserves a retained local selector and its Codec owner.
- Per-call Ack before a local producer, retirement while that Ack is held, and continued
  physical ownership after logical cancellation and source/collectible writer disposal.
- Original response Codec across local retirement/cancellation, then a fresh proxy using
  only the replacement Codec. Live-client/server old ALC collection after physical release.
- Duplex holds both original owners at once, then consumer exits while producer cleanup
  still retains its own lease and original linked CTS; both counters eventually reach zero.
- Queued Server/Duplex registration stays protected after consumer exit and starts no
  producer when canceled; zero-grace unregister honestly reports unreleased physical work,
  and Stop joins it through asynchronous disposal.

The Action and offline archive checks must match exact names, uniqueness, count, report kind,
transport and tested SHA. Any retained-control failure, changed guard order/capture point,
producer start before Ack, CTS/module release before physical exit, wrong Codec/generation,
ALC failure or watchdog stops that subject. Preserve every original run; repair a concrete
cause before a new subject. No retry-to-green, assertion/inventory/GC-budget relaxation or
local RPC workaround. Keep the unresolved ordinary Windows #387 Stop result and paused
investigations unchanged. Final package/API review, canonical regression, broader integration,
Trim/NativeAOT and preregistered GitHub performance acceptance remain with #775/#805.

Frozen new inventory: **72 exact local-module cases** in
`eng/775-slice/local-expected-cases.json`, committed before the first hosted subject.

## First hosted subject: stopped on two counterexamples

Source e837e2d460d3cb95ac75e78ecbde085401b3b56a / run43561156674 passes the prior five
inventories on both hosts/transports. New local TCP stops: Windows old ALC remains after
the policy/unrelated-publication case; Linux reaches retirement while Ack is held, then
waits for producer entry until the original240-second watchdog. Both ZIPs/raw logs retained.
The next subject adds diagnostics: recognize the new ALC case name for failure-only heap
root capture, and observe early call failure while awaiting producer entry. A watchdog
failure also captures hosted stacks. Cases/assertions/budgets stay fixed; this is diagnosis,
not an unchanged retry or acceptance. No other implementation line is expanded.

Diagnostic subject30b9edb997d43079f261a21d0e4d370f4bd39158 / run43561787138 fails the
same policy ALC check on both hosts, with root/stack text retained. The Windows stack
shows LocalAlcAsync's collector invoked inline by LocalPolicyAsync.SetResult, itself still
inside collectible LocalDriver.Output and module/dispatcher generic MoveNext completions.
The checker is running on the very consumer frame whose unload it tests. Add an explicit
ForceYielding await boundary before collection; no extra GC iterations, sleep, service stop,
case removal or budget change. The diagnostic subject does not reach the producer case,
so its first-run timeout remains unresolved pending that case's early-terminal observation.

Subjectbd0dc58578a994a6b72ea983b3143f693013d48b / run43562276011 demonstrates that
ForceYielding was insufficient: its captured Windows stack still calls the collector inline
from the unfinished case's SetResult and collectible consumer completion. ForceYielding's
already-completed await check does not make a future Task completion asynchronous. Replace
that fixture handoff with an explicitly queued default-scheduler continuation whose own
continuations use RunContinuationsAsynchronously. Keep the exact10-GC/live-owner condition;
no product fix is inferred from this fixture scheduling counterexample.

Subjectfcdb736f0da07e8d2a9f2704dde3c996482ce587 / run43562718239 passes the unchanged
policy ALC check on both hosts. The producer early-terminal observation now exposes the
original timeout's hidden result: server Internal service error before producer entry,
not a producer that ignores cancellation. Add a fixture mapper that logs the original
server exception and delegates unchanged to DefaultRpcExceptionMapper(false); preserve
client-visible mapping and all72 controls to identify the cause before changing production.

## Correct the borrowed-head lifetime before accepting green controls

Subject52bcdca3c6cd30b01cfa6844295dd4a9bc038f23 / run43563117277 reports72/72 on all four
cells, but its original Windows TCP log contains ArgumentOutOfRangeException at generated
Stub SequenceReader construction after held acceptance. Cancellation happened to win the
client terminal, hiding that server failure. This subject is NOT accepted. The original
linux timeout/fourth-subject Internal are consistent with the same invalid borrowed head.

Per-call Ack adds an await before generated argument decoding; an uncompressed Request's
Pipe-backed arguments (including empty slice positions) can expire at AdvanceTo. Fix the
profile in existing DispatchRpc preparation after call admission: canonicalize empty stream
arguments to ReadOnlySequence.Empty, copy nonempty stream arguments into the existing
bounded buffer/call-state payload owner before invocation, and use its established release
path through decode/invocation/disposal. Compressed heads already have their owned buffer;
ordinary Unary/OneWay compilation is unchanged. No latest registration/Codec lookup is added.

Before the corrective subject runs, retain the original72 cases exactly and freeze four
additional `local head ...` controls (total76). They hold a real nonempty generated head,
complete a later real Unary on the same transport while decode is held, then verify the
original header/result/Codec and no hidden mapped error. All case bodies now reject an
unexpected mapped server exception even if client cancellation won. The diagnostic mapper
continues to delegate unchanged to DefaultRpcExceptionMapper(false). Original watchdogs,
GC budget and stop rules stay unchanged. This is a corrective evidence extension, not a
replacement or weakening of the original72 inventory.

## Result: accept the local-generation slice

Final tested source **8985bc54076c15f7261051d526967b34c7ea787c** passed
[run 43563783002](https://github.com/SunSi12138/SharpLink/actions/runs/43563783002).

| Host / .NET 10.0.15 | TCP | SharedMemory | Job |
|---|---|---|---|
| Linux x64 | 99 + group67 + receipt49 + facts49 + streams64 + local76 | same six exact inventories | 132353041910 |
| Windows x64 | 99 + group67 + receipt49 + facts49 + streams64 + local76 | same six exact inventories | 132353042017 |

The original 72 local cases are retained exactly, with the four corrective head controls
frozen before this final subject. All 24 final reports pass exact names, uniqueness, count,
kind, transport and source checks, hosted and offline. The complete six-subject archive has
128 reports, each checked against its own committed source inventory (72 only where that
was the tested inventory). Both final raw logs contain no mapped server errors. The earlier
control-green 52bcdca3 subject remains unaccepted because its logs expose the decoder fault.

Both hosts pass ordinary/profile/plugin basic builds, exact old-Stub CS0535 negative consumer,
wrong-ABI preflight, the one-cold-resolver source guard and five admitted-dispatch source
controls. Ordinary compilation tokens/build inputs remain unchanged across 68 shared/changed
production files versus 2360a0ca. Ordinary Unit evidence remains Linux 2212/2212 and Windows
2211/2212 with the unresolved #387 Stop failure; it was not rerun or relabeled as passing.
Local work was development/basic builds/source and archive checks only. No performance ran.

Accepted evidence covers the original wrapper order, invocation-time policy/deadline and
first-enumeration acquire; exact local selectors across unrelated publication; local retirement
before/after Ack; original producer/consumer Codecs and both cancellation sources; independent
physical owner exits; queued registration retention; honest timeout/Stop drain; seven old ALC
checks with live client/server and retained scalar channels. The added head preparation uses
the existing call-state buffer owner, so its physical release follows the original invocation
and service cleanup rather than Ack or logical cancellation.

**Next: OneWay input's bounded shape/refusal-cleanup slice.** It remains explicitly fail closed
here. Preserve the existing typed writer capability when selecting its final grammar, then
complete stream-specific error/authorization/backpressure combinations, multi-Session Picker/
reconnect, #931/#933/#964 remaining cleanup, final caller/package/API inventory and independent
#805 review. Canonical regression, Trim/NativeAOT and preregistered GitHub-only performance
acceptance remain open; no PR/merge, no paused investigation restarted.

Development source remains in `C:/Users/sunsi/Desktop/SharpLink-775-Slice`, branch
`codex/775-static-bind-slice`. The archive at
`C:/Users/sunsi/Desktop/SharpLink-775-evidence/local-20261114/` retains the complete source
bundle, six runs' twelve original ZIPs/twelve raw logs, diagnostic root/stack text (not heaps),
run metadata, 128-report index, both frozen inventories, original criteria, workflow snapshot,
issue snapshots and restoration README. Only reports and temporary workflow removal change
after the tested subject; remove its remote CI branch with an exact-head lease after capture.
