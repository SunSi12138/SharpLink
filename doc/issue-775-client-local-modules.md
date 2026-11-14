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
