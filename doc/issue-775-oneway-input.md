# #775 bounded OneWay input and refusal cleanup

Base: `387a04af3fc24ec7518696566645c6112a087b69`, the accepted client-local handoff.
Product dev is `160d0cd9a0550de5bf00c63b6007376c02debc20` (checked before this subject).
This is the seventh bounded, nonshipping development slice, not a production merge or
full #775/performance acceptance. Paused investigations remain paused.

## Independent bounded shape, without a per-call Ack

Private frame 245 carries `Count:u8 (1..127)` followed by the existing Request envelope:
BindingId:u64, MethodId:u64, optional budget/metadata, then typed business arguments.
Count and routing are never business-compressed. Ordinary Unary/plain OneWay keep their
16-byte routing prefix. Do not reinterpret a BindingId, metadata field or HasReturn bit
as a shape. The parser requires OneWay, a nonzero correlation, complete routing, and no
return/error/truncated flags. Known bound facts must exactly match Count before decode.

The reader validates the full frame envelope, strips Count, then reuses the existing
Request admission/queue/decode/typed Stub pipeline. Unknown binding/method and call-capacity
refusals use Count to install bounded discard routes without querying a latest Stub or
executing a business Codec/provider. Compressed discard uses validated original lengths
for receive credit. Existing per-Session route caps and negotiated receive windows remain
hard limits; exhaustion/protocol abuse terminates the offending Session and joins cleanup.

The generated typed writer remains the producer. OneWay starts it after local head
publication, not RequestAccepted. Its completion still means local enqueue/source
completion, not server business completion. A published OneWay pending call rejects
RequestAccepted. Keep original wrapper acquire-before-inner-guard order and inner
Codec-null/identity/cancellation order; do not impose another family's Ack semantics.

## Keep original physical owners through cleanup

Reuse the pending producer's reference-counted CTS and physical producer counter for
OneWay. Retain the already acquired local module owner through producer/writer disposal;
do not acquire a retired gate again. Logical cancellation may remove the pending slot
while noncooperative MoveNext/Dispose/collectible cleanup still owns its original CTS,
Codec and registration. Stop and module drain must join that work.

On the server promote the original bound module protection to a stream lease. Own a
nonempty uncompressed head before asynchronous activation/interceptors (compressed heads
already have bounded ownership). When OneWay business code exits early, keep stable
discard routes until peer terminal, but abandon and physically join any old typed child
and active dispatch before service/module release. Do not remove the discard route to
pretend that physical cleanup or receive credit has completed.

Private protocol identity changes to Magic 0xBC and locator identity
`sharplink-experimental-775-oneway-input-v6`. The previous v5 locator fixture must be
rejected in metadata preflight without constructing a manifest/Stub. Frame 245 is not a
production allocation; typed generated writer/readonly facts ABI is unchanged. Ordinary
product tokens and build inputs must remain identical to the accepted ordinary baseline.

## Acceptance frozen before the first hosted subject

All RPC/runtime/lifecycle/GC execution is a temporary GitHub Action on Windows/Linux x64
and real TCP/SharedMemory. Local work is development, basic builds and source/archive
checks only. No performance experiment is required for this functional slice.

Keep inventories 99/group67/receipt49/facts49/streams64/local76 and their original
60/120/120/120/180/240-second watchdogs. Before the first Action, migrate exactly these
three obsolete assertions, with no count reduction:

- streams: `stream OneWay input remains explicitly fail closed` becomes
  `stream generated typed OneWay input completes without RequestAccepted`;
- streams: `stream unsupported OneWay input starts zero producer` becomes
  `stream enabled OneWay input starts exactly one producer` (also await both received items);
- facts: `facts OneWay client stream shape is retained without transport enablement`
  becomes `facts OneWay client stream shape retains complete count and cancellation facts`
  (the scalar assertion is unchanged).

Other 62 stream controls and 48 facts controls, and all other retained inventories, remain
unchanged. Group fixture gains a typed OneWay method, not relaxed assertions.

Freeze a separate **74-case** exact inventory in `eng/775-slice/oneway-expected-cases.json`:
grammar/count/flags/correlation, inner guards, generated plain/compressed typed calls and
no Ack, early return, wrong binding and wrong method (plain/compressed), multi-window
credit return, call and route exhaustion, malicious compressed original-length overflow,
known-count conflict, cancellation/Stop, local forced retirement and producer cleanup,
server original Codec/service cleanup, and two live-client/server ALC reclamation checks.
OneWay process watchdog: 240 seconds. Synchronous Codec cut: 15 seconds. Normal module
grace: unchanged 30 seconds; forced local retirement: zero grace. ALC: existing ten full-GC
iterations, with queued checker handoff and live client/server/scalar channel/proxy. Do not
Stop clients to obtain a passing ALC check. Small-window fixture uses stream64/connection128
bytes and four routes; refusal sends 16 x 32-byte chunks (eight connection halves).

Any retained or new control failure, wrong Codec/provider execution on refusal, leaked
credit/route, Ack emission/gating, guard drift, early CTS/module release, ALC failure,
unexpected mapped server exception (even with a green client terminal), missing source-
bound report, or watchdog stops that subject. Preserve every original run and failed
artifact. Fix a concrete cause before a new subject; no unchanged rerun, retry-to-green,
budget/GC increase, inventory relaxation, local runtime execution or fallback to latest
registration. Workflow job limit remains 20 minutes. Every report must match exact names,
uniqueness, count, kind, transport and tested SHA.

## Result and remaining obligations

Hosted verification has not started at this freeze. Required ordinary Windows Unit Stop
failure (#387, 2211/2212; Linux 2212/2212) remains unresolved and paused; exact ordinary
token/build-input parity permits reuse of that evidence, not a passing full-regression claim.
This slice does not complete production API/package/caller migration, independent #805
review, canonical generation, Trim/NativeAOT, broad error/auth/multi-Session combinations,
or the final performance gate. #775 and #805 remain open; no PR/merge is implied.

## First counterexample and corrective freeze (before the second subject)

First subject `a463a142c22d56af2b8c74788eb50394aa5f0dd4` /
[run 43567231843](https://github.com/SunSi12138/SharpLink/actions/runs/43567231843)
fails on both hosts at the original 240-second watchdog in the over-window TCP control.
All six retained inventories pass all four cells; new OneWay controls reach the route-quota
control before the hang. Both original ZIPs, raw job logs and failure-only stack/root text
are preserved. No new OneWay JSON exists for that failed process.

The reader's old compressed-input branch bypasses a stable mailbox whenever a call state
exists. OneWay can already own a call state while its interceptor is held and its typed
consumer is not attached. The first buffered candidate is instead sent to the provider;
a corrupt-provider error completes that input route rather than exercising the intended
receive-window rejection. Waiting for a Session terminal then hits the frozen watchdog.
This is a missing OneWay pre-terminal buffering boundary, not evidence for increasing time.

Route owned OneWay compressed input through its existing stable mailbox even with a call
state. Reserve original-byte credit before provider execution; keep compressed buffering
before the interceptor terminal. The mailbox carries a scalar request/map progress callback
(no collectible registration or pooled state pointer), reclaims progress after a successful
provider call, and repays accepted bytes on provider failure. A cooperative provider observing
this call's deadline is not a Session failure, and the read-loop error path must not charge
or consume those accepted bytes twice. Other three streaming families retain their old
read-loop arbitration path; ordinary compilation tokens remain unchanged.

Before the corrective subject, retain all original74 cases exactly and add **eight**
controls, for **82** total: cooperative/noncooperative provider held across a deterministic
five-second server deadline; no typed item Codec while held or after terminal; original
credit/routes reclaimed; unrelated Unary/Session still usable. Providers use the existing
RLE fixture; only the deliberately noncooperative test ignores cancellation. Existing
watchdogs, 15-second synchronous cut and ten-GC limits are unchanged. Extra phase logging
identifies the original over-window cut; it is not a new acceptance condition.

Second subject `e948550f65135efc8713951da1fa475651a1053f` /
[run 43568390966](https://github.com/SunSi12138/SharpLink/actions/runs/43568390966)
passes the original over-window cut on both hosts: provider0/typed Codec0, then physical
route cleanup. All retained inventories still pass four cells. The added noncooperative
provider deadline assertion fails: the test only configured a client-wide fallback, which
OneWay intentionally does not inherit without a declared timeout. The fixture therefore
had no wire deadline to cross. Correct the fixture with parameterless [Timeout] and assert
that the received original deadline exists; do not change the original OneWay fallback
contract. The full82 inventory and all budgets remain unchanged. Extra count/phase logging
will distinguish later provider/Codec failures. Both original runs remain unaccepted.

Third subject `79b1d8e0179526786ba3de79ac23547f3ae52676` /
[run 43568782428](https://github.com/SunSi12138/SharpLink/actions/runs/43568782428)
passes both provider/deadline controls and the original cancellation/Stop/physical producer
checks. Its local ALC gate fails. Linux LoaderAllocator root `alc-root-7bdef040ea20.log`
shows the live Session -> StreamFlowController -> send-state dictionary -> SharpLinkException
-> original OperationCanceledException -> collectible stack -> LoaderAllocator. This is a
real framework retention path, not the prior checker-inline-stack problem.

OneWay abandonment consumed a small input but left its WindowUpdate below threshold, while
logical cancellation correctly suppressed a late StreamComplete. A completed send state
retained its exception waiting for that credit; even pooling only cleared AbortException on
next rent. Fix the physical boundary, not the publication guard: flush already-consumed
OneWay credit after local abandonment WITHOUT marking peer terminal or removing the stable
discard route, and clear AbortException when a fully credited completed state enters the
pool. Existing routed/active waiters still own their original error independently. No late
stream terminal, extra Ack, retry/rebind or connection close is introduced.

Before the fourth subject retain all82 cases and add two (84 total): sub-threshold credit
returns while the original Session stays connected, and no pooled cancellation exception/
collectible stack remains. Wait for actual credit quiescence before the unchanged ten-GC
live-owner check; this is a physical completion condition, not extra GC or a fixed sleep.
The scalar discard route remains bounded until peer terminal/Session teardown, independently
of the old typed registration and Codec. Existing 240s/15s/10-GC budgets stay unchanged.

Fourth subject `0f2fb3b565226c302845ed42fa5b5265fb780e64` /
[run 43569415979](https://github.com/SunSi12138/SharpLink/actions/runs/43569415979)
passes all seven inventories in all four cells, including84 OneWay controls, both ALC gates,
credit quiescence and pooled-exception clearing. Neither host log has a mapped server error.

Final source readback finds one remaining cooperative-decoder error-path race: releasing the
active child can let the invocation remove its pooled call state before the read-loop catch
re-queries the map. "No state" normally means pre-admission and could turn an already selected
call cancellation into a Session failure. Capture that terminal decision BEFORE child release
and carry an internal cancellation marker to the read loop. The original cancellation is its
inner exception; this is not a public exception contract or a new wire frame. The cooperative
provider controls exercise this path. Keep the same84 names, conditions and budgets for the
final changed-source subject; do not rerun an unchanged candidate for reassurance.
