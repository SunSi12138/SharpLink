# Protocol3 contract binding: selected technical specification

**Status: selected design for [#775](https://github.com/SunSi12138/SharpLink/issues/775), not an implemented, validated, released, or shipped protocol.** Source preparation does not establish interoperability, performance acceptance, completion of all five RPC families, or adoption of physical-ownership changes. This document fixes one breaking wire/API direction.

The wire identity is **ProtocolVersion 3, handshake revision 1, magic `0x8A`, generated API version 5**, with generated schema identity `sharplink-3.0-api5-contract-channel-complete-method-facts-v1`. The revision of this design document is not the handshake revision. Generated API5 is the single release boundary after published 2.0.3/API4; unreleased full-facts and channel drafts do not create additional released API epochs. The distinct exact ABI identity rejects incompatible API5 development artifacts. Protocol3 starts its own revision domain at 1; published Protocol2 minor 4 is not a same-domain predecessor or downgrade target. There is no Protocol2 parser fallback, legacy grammar probe, downgrade, compatibility mode, or optional binding capability. Historical old-wire byte fixtures remain negative compatibility controls; they must not be rewritten into new-wire success fixtures.

This is a companion to [the contract-channel API proposal](issue-775-clean-contract-channel-design.md), not a replacement or an edit of that proposal. It consolidates the wire grammar, finite preparation, bounded publication, invalidation, and ownership requirements into a self-contained technical reference. Terms such as “must” describe acceptance requirements, not claims about currently shipped behavior. Error detail reservations below remain subject to collision verification against the integrated source/API inventory.

## Public API and generated ABI boundary

The public client is a lifecycle/control/proxy factory, not an identity-neutral invocation channel. Remove the old `IRpcChannel`, raw client invocation facades, implicit default identity, old `Get`/`GetWithMetadata` aliases, and their generated-ABI acceptance. Generated and handwritten calls share one `IRpcContractChannel` implementation pipeline. `RuntimeContext`, custom typed codecs and stream writers, request metadata, deadline/cancellation, interception/retry, selector/admission, and collectible ownership capabilities remain available.

```csharp
public interface ISharpLinkClient
{
    IRpcRuntimeContext RuntimeContext { get; }
    RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null)
        where TContract : IService;
    RpcLocalContract GetLocalContract<TContract>() where TContract : IService;
    RpcLocalContract GetLocalContract(System.Reflection.Assembly registeredAssembly, long contractId);
    IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null);
    // Existing lifecycle, registration, and configuration capabilities remain.
}

public sealed class RpcProxy<TContract> where TContract : IService
{
    // Constructor is internal: Service and its exact channel cannot be mismatched.
    public TContract Service { get; }
    public ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
}

public interface IRpcContractChannel
{
    IRpcRuntimeContext RuntimeContext { get; }
    ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default);
    // All five existing typed Invoke* families retain their complete generic,
    // in/ref, codec, stream-writer, metadata, cancellation, and return shapes.
}
```

`RpcLocalContract` is a sealed client-issued scalar selector with an internal constructor. It identifies one exact issuer, local registration generation, nonzero signed64 ContractId and full nonempty assembly hash. It is not a remote token, lease, credential, mutable “current” identity, or caller-supplied Session. The explicit assembly overload accepts only the exact registered assembly object and its validated current generation; it cannot search another owner for a matching numeric ID. Escaped selectors/channels never auto-upgrade after replacement.

`RpcProxyOptions` captures scope, metadata, requirement (`Compatible`, `MinimumVersion`, or `ExactRegistration`) and an optional receipt immutably. A receipt contains ServerInstanceId, RegistryEpoch, the registration's own publication generation, RegistrationId, ContractId and assembly hash. The channel freezes that complete identity and requirement once, rather than hashing or selecting it again on every call. Method descriptors retain their semantic facts and must match the channel's ContractId and shape; they are not serialized as binding authority.

Factories are synchronous local construction: validate input, terminal state, issuer/exact local generation, descriptor/hash/codec ownership, then reserve bounded demand/cache ownership and construct the exact wrapper. They do not await Connect, Manifest, or Bind and do not serialize business arguments. Failure rolls back reservations. A live receiver retains its demand, but weak controller/cache entries must not indefinitely root collectible code. Local retirement, stop, and quota behavior remain explicit.

Both factories atomically register bounded cold demand before returning. Prepare attaches a bounded waiter to that same demand/channel; it does not resolve the latest local contract or install a second identity. Demand may converge without Prepare. Business calls remain FailFast/NoBoundRoute until a real confirmed eligible route exists, before serializer or producer work. A successful Prepare is not a call-capacity reservation or a registration lifetime lease.

Generated factories and current manual consumers use only the API5/Protocol3 schema tuple above. An old API5/Protocol2 or otherwise mismatched generated manifest must fail before a proxy/stub factory runs. Current-source migration must preserve complete method facts rather than erase them to obtain compatibility.


## Bootstrap and common encoding

Magic **0x8A**, header15 bytes: magic@0, payloadLength signed little-endian i32@1, typeu8@5, flagsu8@6, correlationu64LE@7. Negative/overlimit payload, unknown type/flags, wrong direction/phase/correlation domain close Session without business decode. **0x89 and all other magic reject at header**, never try another parser. Old Protocol2 and #861’s 36-byte grammar are retained negative fixture bytes, not recompiled/migrated into new frames.

Protocol3 initial handshake **Revision=1 exactly**, no range downgrade. Binding is intrinsic, not an optional capability bit. Supported/required capabilities retain useful optional metadata0,compression1,flowcontrol2,health3,cancellation-reason4,manifest5,session-refresh6 bits; bits>=7 unknown and rejected in required/negotiated sets. Protocol3 implementations require flowcontrol, cancellation-reason and manifest for this complete RPC model (bits 2|4|5); authentication/metadata/compression remain separately enforced. Cancellation is always the single reason-byte grammar; Unspecified0 remains a meaningful reason, not permission to omit the byte. No old empty-Cancel parser.

All multibyte integers LE. ContractId/MethodId preserve signed64 bit patterns; token/operation/generations are unsigned. Hash is Highu64LE then Lowu64LE, the hash is rejected only when High == 0 && Low == 0. GUID encoding is exactly .NET Guid.TryWriteBytes default 16-byte layout (first4/2/2 little-endian, final8 unchanged), explicitly identical for every Protocol3 authority/receipt/Manifest/Refresh field. Canonical varuint32 is1..5 bytes, fifth high nibble zero, no redundant terminal zero; truncated/overlong rejected. UTF8 is strict, metadata/error field limits retained. No business codec, service activation or user compression provider is invoked for handshake/binding-control parsing.

## Complete handshake payloads

Request type 0, flagsNone,id0, only Handshaking/client→server. Fields in exact order:
- Revisionu16@0 (=1); Supportedu64@2; Requiredu64@10; MaxFramei32@18; StreamWindowi32@22; ConnectionWindowi32@26; ResponseCompressionPreferenceGenerationu64@30; AllowResponseCompressionu8@38 (0/1).
- **BindingLimits16@39..54** (below).
- CompressionProfileCountu8@55,0..16; each Lengthu8(1..64) then validated ASCII token; unique ordinal names, profile bytes must each be in the inclusive ASCII range 0x21..0x7E. Count 0 is valid if compression unrequested.
- AuthLength canonical varuint32 then exactly AuthLength opaque bytes; auth<=local MaxMetadataBytes and complete frame<=localMaxFrame. No trailing bytes. Authenticator called only after full structural validation/negotiation constraints, before SessionReady.

Success response type 1,flagsNone,id0, only Handshaking/server→client:
- Revisionu16@0 (=1); NegotiatedCapabilitiesu64@2; MaxFramei32@10; StreamWindowi32@14; ConnectionWindowi32@18.
- **Authority48@22..69** = ServerInstanceIdGuid16, RegistryEpochGuid16, negotiated BindingLimits16; GUIDs nonempty.
- CompressionProfileLengthu8@70 (0 if no compression), followed by exactly0..64 ASCII bytes; total length71..135, no trailing bytes.
- Chosen capability set subset of offered supported and includes both sides' mandatory requirements; response bound limits positive and <=offer/local policy, not silently larger. MaxFrame/windows use existing safe negotiated min semantics/validation. Authority stored on this exact physical Session; same authority on replacement Session does not transfer tokens.
Handshake failure type 1 flagsError(+Truncated), id0 uses error payload, no success fields. Close after one bounded error or immediately if write unavailable. Early business/control frames during Handshaking fail protocol state. A new peer receiving old0x89 rejects before reading these offsets; an old peer rejects new0x8A. No probing fallback.

BindingLimits16: Slotsu32@0, Operationsu16@4, MaxClientStreamsPerCallu16@6, OperationDeadlineMsu32@8, QueuedControlFramesu16@12, reservedu16@14=0. Default 4096/64/127/30000/256; valid1..65536/1..64/1..127/1..60000/1..256. Negotiated elementwise min; reject invalid values, never clamp malformed input. Real per-session control bytes are separately bounded by queued frame count×largest binding-control frame99 bytes (max25,344 at 256 frames); authentication/profile frames use handshake/frame budgets, not that queue budget. Global quotas below constrain aggregate state regardless configured Session count.

## New control frames and exact payloads

All binding-control types 17..21 have flagsNone and no compression/metadata. Use SAME serialized bounded NORMAL send queue, preserving confirmed Bind publication before CancelBind/Release, never the protocol-progress priority queue. Header operation domain17/18/21:1..Int64.MaxValue, monotonically increasing per physical Session and never reused. Token frames19/20:id0. Business request IDs remain a separate existing pending-call domain. Control IDs never index/expand arrays directly.

| Type | Direction | Payload bytes / semantics |
|---|---|---|
|17 BindContract|C→S|28 no receipt /76 receipt: requirementu8@0(0Compatible,1Minimum,2Exact), hasReceiptu8@1, reservedu16@2=0, Contracti64@4, HashHighu64@12,HashLowu64@20. Receipt adds ServerGuid@28,EpochGuid@44,Generationu64@60,Registrationu64@68. NoReceipt requiresCompatible and no extra fields; receipt GUIDs/generation/registration nonzero.|
|18 BindResult|S→C|84: statusu16@0,reservedu16@2=0,Tokenu64@4,Contracti64@12,HashHighu64@20,HashLowu64@28,Registrationu64@36,Generationu64@44,ServerGuid@52,EpochGuid@68. Success requires all identity invariants and exact request key/Session authority. Failure requires all bytes4..83zero.|
|19 BindingInvalidated|S→C|12: Tokenu64@0,Reasonu16@8(1RegistrationRetired,2Evicted,3SessionDraining),reservedu16@10=0.|
|20 ReleaseBinding|C→S|8: Tokenu64@0. Only matching captured full token removed, old token no-op; entry never retargeted.|
|21 CancelBind|C→S|0: header operation ID identifies published demand only; never cancels a newer operation by reused slot/key.|

BindResult statuses 0..12: Success,UnknownContract,HashMismatch,ReceiptScopeMismatch,BelowMinimum,RegistrationMismatch,RegistrationRetiring,BindingCapacity,ControlCapacity,ControlDeadline,ScopeDenied,CancelledBeforeCommit,SessionDraining.13+ reject malformed. Server accepts well-formed Bind in Ready; Draining performs bounded parse/correlation then final SessionDraining result without new binding or business admission. Handshaking/Terminal traffic is illegal. Cancel/Release and late results may finish existing ownership while Draining. Terminal closes/releases state; no new dispatch from a previously captured entry.

Token = incarnation48<<16 | slot16. slot<negotiatedSlots before one immutable entry read; incarnation1..2^48-1, nonzero. Full token never repeats within Session: on next incarnation overflow, reject new binding / gracefully replace and drain Session, no wrap. Slot can recycle only by publishing a NEW immutable entry; still-captured old entry cannot refer to new registration. Different published BindIDs receive distinct tokens even for same key; only client live exact-key demand coalesces.

## Business and stream envelope

Request4 retains useful flags HasTimeBudget(bit 2),HasMetadata3,Compressed4,Cancellable5,OneWay6,HasReturn7; Error (bit 0) and Truncated (bit 1) are forbidden. OneWay+HasReturn forbidden. Prefix exactly BindingId64+MethodId64 (16 bytes). If time budget, append i64 ticks(100ns), nonnegative; zero means expired, not infinite. If metadata, append canonical varuint byte length then exactly that metadata block. Metadata block: varuint count, repeated varuint keyUtf8Length+key bytes+varuint valueUtf8Length+value bytes; entry count <= Int32.MaxValue and <= remaining block bytes / 3 before allocation, nonblank keys, strictUTF8, no trailing bytes. Then business body. Only business body may be compressed: u32 originalLength followed by negotiated compressed bytes; all routing/time/metadata bytes remain uncompressed. Sum prefix+originalLength<=negotiatedMaxFrame; input compressed length<=frame limit; metadata capability required. Do not call decompression provider until exact binding/protection/method/admission gate authorizes business decode.

OneWayStreamRequest23: same envelope except fixed prefix is **18 bytes**, BindingId64+MethodId64+ClientStreamCountu16, count 1..negotiatedMaxClientStreams and <=127. OneWay flag REQUIRED, HasReturn forbidden; others as Request. All prefix walkers/writers (frame parser, compression GetBusinessPrefixLength, ServerRequestEnvelopeReader Read/ReadDecoded, request rejection) must accept type-dependent16/18 explicitly. A normal Request OneWay whose resolved method declares client streams is shape violation; it cannot guess missing count. Valid method must match exact wire count. On invalid token/unknown method, count is usable without stub/codec to install bounded discard/credit cleanup, or Session closes when quotas cannot safely discard. No per-request OneWay ACK.

RequestAccepted22: S→C, flagsNone, existing business request ID(nonzero), **empty payload**. Valid only for an existing ClientStreaming/Duplex pending-call state on same Session after binding/lease/facts/pre-request authorization/capacity and all stream routes accepted. Exactly one acceptance transition; duplicate/unknown ACK is protocol violation except a late already-terminal allocated ID, ignored using the monotonic request-ID high-water rule specified under Numeric ownership bounds and correlation below. No producer before the atomic acceptance-vs-cancel/deadline winner; accepted producer continuations scheduled asynchronously, not arbitrary user execution inline on receive loop. ACK is not proof subsequent business decode/invocation succeeds. ServerStreaming no client producer needs no ACK.

Established StreamData7 /StreamComplete8/WindowUpdate9 keep exact stream ID+request correlation and original Session/registration ownership; no rebinding per item. StreamData compresses only bytes after u16 streamId; control/error payloads never compressed. Data for undeclared streams fails closed or follows a proven bounded discard record, never allocates arbitrary wire IDs. Response5 error/success and GoAway10 preserve binary error envelope: topErroru16 +detailu16 +canonicalVaruintUTF8Length +strictUTF8 bytes; Truncated only withError; errors never compressed. Existing Ping/Pong/Health/Manifest/response-compression/session-refresh use the exact control grammars defined here, renamed Protocol3, no Protocol2 parser retained. Binding cannot bypass auth or request interceptors.

## Remaining type 0..16 closure (one Protocol3 parser)

| ID | Direction / correlation | Allowed flags | Exact payload |
|---|---|---|---|
|0,1|handshake as above/id0|None; responseError+optionalTruncated|new exact handshake or binary error|
|2Ping,3Pong|both/id0|None|i64 monotonic stamp,8 bytes|
|4Request|C→S/nonzero businessID|bits 2..7 with exclusions above|16-byte binding envelope|
|5Response|S→C/nonzero businessID|Error,Truncated,Compressed (Error+Compressed forbidden)|binary error or successful business bytes; original call facts determine expected payload|
|6Cancel|both/nonzero businessID|None|exact1byte reason0Unspecified,1UserCancellation,2DeadlineExceeded,3ConsumerAbandoned; no others|
|7StreamData|both/nonzero businessID|Compressed|u16streamId+business bytes; compressed body usesu32original length|
|8StreamComplete|both/nonzero businessID|Error,Truncated|u16streamId only on success; u16+binary error otherwise|
|9WindowUpdate|both/nonzero businessID|None|u16streamId+u32credit,exact 6 bytes,credit1..Int32.MaxValue|
|10GoAway|both/id0|Error,Truncated|lastAcceptedRequestIdu64+binary error; Error flag required for error envelope semantics|
|11HealthCheck|C→S/nonzerohealthID|None|empty|
|12HealthResponse|S→C/nonzerohealthID|None|exact1byte Unhealthy=0, Ready=1, Draining=2|
|13ContractManifest|S→C/id0|None|Generationi64>=0+Counti32>=0 then Count×24 bytes (Contracti64,HashHighu64,HashLowu64), exactly12+24count; sorted unique nonzero contract IDs/nonempty hashes, <=MaxFrame|
|14ResponseCompressionPreferenceUpdate|C→S/id0|None|generationu64+allowu8(0/1),9 bytes|
|15ResponseCompressionPreferenceAck|S→C/id0|None|appliedgenerationu64,8 bytes|
|16SessionRefreshRequested|S→C/id0|None|ServerGuid16+DesiredGenerationu64>0,24 bytes; authority matches physical Session handshake|

Existing structured health/reply/direction semantics remain required control semantics, not permission for reverse-direction RPC through a client-only server role. RequestAccepted22 belongs NORMAL queue too; it may not overtake admission publication. Only Ping/Pong/WindowUpdate/GoAway are progress-class frames. No binding/acceptance/release/cancel uses progress priority. Error flags cannot co-occur with compressed; Truncated withoutError is invalid.

Ready permits the above role-appropriate non-handshake frames and17..23. Draining permits replies/control completion/streams/window/progress, plus bounded parse/refusal of legitimate in-flight Bind and new business request frames (Request/OneWayStream) without new call admission: typedUnavailable for response-bearing, bounded diagnostic/discard for OneWay, close only when safe bounded rejection/discard is impossible. No forced connection abort merely because progressGoAway overtook a previously published normal Bind/Request. Existing pre-drain admitted work and its ACK/replies finish. New HealthCheck/ContractManifest publication is not a route to new admission during drain. Stopping/Terminal accept no new frames; terminal completion/owner cleanup is local, not processing untrusted buffered new dispatch. Handshaking permits only its expected request/response role, then exactly one phase transition. Phase checks must happen before business codec/compression, while identity/method eligibility checks remain before business decode.

Handshake request minimum 57 bytes (39+16+count 1+authLen1), maximum 61+16×65+MaxMetadataBytes, also <=MaxFrame; response success71..135. The maximum formula includes canonical authVarint's 5 bytes; it is a bound, not accepting padding. Existing limits MaxFrame default 4MiB, configurable1KiB..64MiB; MaxMetadata default 16KiB and MaxError default 64KiB remain independently configured but effective payloads never exceed actual frame limit. Error/body lengths are checked before allocation; header+payload additions checked for integer overflow. UnknownRequiredCapabilities/negotiated feature disagreement fails handshake, not delayed business parsing.

# Publication, receipt, and preparation semantics

## Prepublication progress budget

The client operation deadline starts at T0 when operation quota+ID are reserved, BEFORE the Publishing record is installed and before normal-queue enqueue can expose bytes. Capture one monotonic deadline T0+negotiated OperationDeadlineMs. It covers record install, publisher queue wait, send backpressure, confirmed enqueue, reply and required cleanup. Before quota reservation, demand scheduling has no operation/ID; an explicit Prepare waiter already has its own finite deadline. Capacity is acquired nonblocking or from a bounded queued demand slot, never by allocating unbounded waiting tasks. The server independently anchors its operation timer at successful structural receipt/operation admission, covering registry lock/queue/commit/final response. Clock epochs are not compared between peers.

At timeout/cancel before enqueue, only a **definitive never-visible rejection** permits retiring the unpublished operation without server cleanup. The actual SendPump enqueue result/ownership gate, not task state or cancellation request, establishes this fact. A concurrently accepted enqueue or buffered early result proves possible visibility; mark logical wait failed but retain protocol cleanup ownership. If remaining operation budget suffices and definitive outcome arrives, send CancelBind or exact-token Release as appropriate, then retire only after required cleanup publication. If deadline expires while visibility/cleanup is uncertain, terminal-close that exact Session to reclaim both peers' protocol state; no ID reuse, no orphaned pending send task, no pretending CTS cancellation retracts bytes. Physical send/compression/cleanup owners remain until true completion even when Session logically closes. This fail-closed error is distinct from graceful ID exhaustion; it is a failed bounded protocol operation, not normal rollover.

Publishing accepts at most one validated early result for its exact record/key. Confirmed enqueue is still required before route publication; a timer or local-generation retirement that won first makes early success cleanup-only. Duplicate/unknown early results follow frozen protocol-violation rules. No timer is reset at enqueue/reconnect/wake; later Session requires a new bounded operation and never extends the original Prepare budget.

## Receipt and candidate acceptance truth table

Let local frozen contract/hash be C/H, receipt Q=(S,E,G,Reg,C,H), and server selected exact current registration R=(C,H,OwnPublicationGeneration,RegistrationId). Every successful Bind requires R currently open for new admission under the publication gate, same requested C/H, applicable target/auth scope, and nonzero valid IDs. Returned token belongs to this exact physical Session; response S/E must equal handshake authority. Server side never trusts Manifest as admission proof.

| Requirement | Receipt? | Wire/local legality | Server additional predicate | Client route eligibility before Bind | Successful response validation |
|---|---|---|---|---|---|
|Compatible|no|legal28byte canonical form|none beyond common predicates|ordinary Manifest advertises C with exactly H within frozen scope|returned C/H and Session S/E exact; R.Reg and ownG valid, do NOT require any absent receipt fields|
|MinimumVersion|no|illegal locally and malformed Bind; no business|not evaluated|none|no success accepted|
|ExactRegistration|no|illegal locally and malformed Bind; no business|not evaluated|none|no success accepted|
|Compatible|yes|legal76byte form; Q.C/H must equal frozen C/H|Q.S/E equals physical Session authority; no generation or registration equality restriction|may actively Bind despite stale/missing Manifest ONLY verified same Q.S/E and scope|returned C/H,S/E exact; valid R.Reg/ownG; may differ from Q.Reg/G|
|MinimumVersion|yes|legal76byte form; Q.C/H exact|same S/E and R.OwnPublicationGeneration >= Q.G|same verified-receipt route exception|returned ownG >= Q.G; Reg may differ, never require equality accidentally|
|ExactRegistration|yes|legal76byte form; Q.C/H exact|same S/E, R.OwnPublicationGeneration >= Q.G AND R.RegistrationId == Q.Reg|same verified-receipt route exception|both comparison predicates; never downgrade requirement to permit a newer registration|

Global Manifest generation growth due to another contract cannot satisfy R's own generation predicate. Matching hash never converts an old token into new R. Different server/epoch cannot compare versions. Delayed success after known token invalidation/local retirement cannot republish; server closed gate remains final even if client did not observe invalidation. Deterministic mismatch surfaces only when no candidate remains ready/pending that could satisfy the original requirement; a single replica's mismatch must not mask another eligible pending match. Aggregate diagnostics stay bounded by current snapshot/quota, not accumulating every past endpoint failure.

## Unified numeric range, distinct semantic generation domains

All **registry publication generations**, receipt/result own-generation, RegistrationId and desired-session-refresh generation use valid positive range1..Int64.MaxValue, even where wire/public container is u64. Reject highbit-set/zero values before comparison. Manifest wire remains i64, accepts0 only for an empty initial registry; nonempty snapshot requires>=1. Initial installed static registrations receive distinct RegistrationIds and own publication generation1; later group transactions use increasing generation. Failed transactions may consume IDs/generations, never reuse them. Binding token incarnation and operation/request IDs have their separately specified ranges, not registry generations.

ServerInstanceId is the one server-lifetime authority also used by desired-session refresh; RegistryEpoch is one nonempty registry-lifetime authority frozen into each successful handshake. They do not silently change within a live server/registry lifetime. At publication-generation or RegistrationId exhaustion, reject NEW Register/Replace/Unregister publication transactions before closing any old gate or changing any service snapshot; existing registrations/calls remain usable under normal stop policy. Do not roll a counter or reset epoch in place. An intentional new server/registry lifetime uses fresh authority identities and new Sessions; old receipts are invalid, not reinterpreted. Identity source must reject empty/reused configured test authority and never offer an API that resets epoch to revive old receipts. No global GUID uniqueness proof is inferred from nonempty bytes; use the host's authority-generation contract, not attacker claims.

DesiredSessionGeneration is a DISTINCT configuration-publication counter with the SAME positive 63-bit range, scoped by ServerInstanceId. It is never compared against registry/receipt generation. Exhaustion rejects further desired-session configuration publication before mutating state; existing desired config remains. ResponseCompressionPreferenceGeneration likewise is a distinct client-local counter, range0..Int64.MaxValue (0initial permitted); overflow rejects new preference update, never wraps. Protocol3 readers/writers enforce each range, not just casts. Server refresh signal must match handshakeServerInstanceId and current desired-session counter domain.

## Mandatory finite Prepare budget and precedence

Both RpcProxy<T> and IRpcContractChannel now expose **ValueTask PrepareAsync(TimeSpan timeout, CancellationToken cancellationToken = default)**. No timeoutless overload and no infinite/default timeout sentinel. Validate0<timeout<=SharpLinkTimer.MaximumDelay; invalid timeout throws ArgumentOutOfRangeException before cancellation/state checks. Next check caller cancellation; then client terminal and exact local generation; then capture one monotonic deadline before readiness snapshot evaluation/waiter allocation. Already-ready returns synchronously only after these checks and a final deadline/cancellation observation under the readiness completion gate, no new Bind.

For a pending waiter, one completion gate arbitrates observations in this priority at its commit: caller cancelled -> budget expired -> client/local lifetime terminal -> deterministic all-candidate Bind failure -> ready. A cancellation that happens after successful ready commit need not retroactively change the completed task; no stronger ordering claim. Timeout yields DeadlineExceeded, caller cancel preserves caller token, local retirement uses exact stable local detail. Quota acquisition failure returns ResourceExhausted without allocating extra waiter. Any event wake/reconnect/rebind retains the ORIGINAL deadline; per-operation Bind TTL is independent and cannot extend it. Detach waiter exactly once; its cancellation does not cancel other waiters or prematurely dispose shared protocol operation.

Construction still registers bounded cold demand, so readiness may converge without Prepare; first business call defaults FailFast. Background demand does not loop forever on a timer: deterministic mismatch is sticky for that exact Session/key until a relevant registry/Manifest/Session event; capacity failure waits an actual capacity event; timed-out operation does not immediately recreate itself in the same generation. A new explicit finite Prepare may trigger a fresh bounded attempt, but cannot revive retired local identity or change requirement. No sleep polling, unbounded retry queue, or restart of caller budget.

# Numeric ownership bounds and correlation

All configuration captured/validated at client/server construction, positive and <=hard ceiling, no live shrink semantics in this slice. Negotiated Session values may only decrease from offer/server limits. Admission reserves before allocation/publication and rolls back on failure; remote numeric values never resize tables. Values are safety ceilings/defaults, not approved performance thresholds.

| State owner | Default | Hard ceiling | Reclaim/overflow |
|---|---:|---:|---|
|Session bindings/slots|4096|65536|fulltoken release/evict/Session close; no long call lease|
|Session pending Bind operations/client ring/server queue|64|64|one final result+cleanup or Session close; expired/cancelled logical waiter alone does not retire|
|Session binding-control queue frames|256|256|max 99 bytes/frame =>25344 bytes; release/final-result enqueue failure closes Session if cannot converge within operation budget; no unbounded pending enqueue tasks|
|Client total pending Bind operations|1024|65536|same terminal wire lifecycle, includes waiterless operations|
|Server total pending Bind operations|4096|65536|reserve before operation object; controlcapacity result if response queue permits, else close|
|Server total live binding slots|65536|1048576|release/evict/disconnect; retired scalar entry must not retain collectible payload|
|Client live demand sites|4096|65536|weak owner cleanup or local retirement/client stop; active operations count until final cleanup|
|Client weak cache entries/tombstones|4096|65536|bounded incremental sweep; no resurrection; quota rejection before extra slot|
|Waiters per site|64|1024|each own cancel/deadline/stop/terminal error detaches once|
|Client total waiters|4096|65536|global reservation+same detach; cannot allocate unbounded Task waiters then reject|
|Controller queued maintenance jobs/client or server|4096|65536|coalesce scalar key, process at most256jobs per pump turn; full queue coalesces dirty snapshot or rejects new demand, never loses retirement gate|
|Session Bind lifecycle deadline ms|30000|60000|client timer from operation-quota/ID reservation BEFORE Publishing installation/enqueue; server timer from valid Bind receipt; budgets never extend on queueing/activity/retry|
|Client streams per call|127|127|also negotiated minimum; global/per Session existing stream capacity still independently enforced|

Invalidation best-effort queue overflow may drop/coalesce notifications because server retirement gate is authoritative; it must not allocate a pending task per dropped token. Existing accepted Bind final result is NOT best effort: enqueue exactly one or close Session. Release is idempotent fulltoken; if required cleanup cannot be queued within bound, close exact Session instead of leaking remote slots. Operation IDs exhausted at Int64.MaxValue reject new binds and gracefully replace/drain Session; never reuse. Token incarnation exhausted at2^48-1 rejects new binding and gracefully replaces/drains Session; never wrap. Empty slots and scalar failed-operation records are bounded by the above tables, not request-supplied numbers.

Client states: Publishing (record/key installed, bytes may become visible), Published, FinalCleanupPending, Retired. One structurally+key-valid early result may be buffered only for exact PublishingID; duplicate early result is violation. No route publish/retirement until enqueue success confirmed. Definite enqueue failure before visibility may retire unpublished ID; uncertain outcome closes Session, never reuses ID. Cancelled waiter does not dispose operation. Early success+local cancellation queues release of that exact created token before retirement. Distinct BindIDs never share a server token, so old Cancel cannot revoke a later demand.

Retired-prefix correlation: client keeps bounded ring plus scalar allocated high-water and contiguous retired prefix. ResultID beyond allocated/unknown nonretired slot is violation. ID<=retired prefix is structurally parsed then no-op, not an instruction to release arbitrary currently-used token. The original operation already queued its success publication or release cleanup before retirement. Server receives strictly increasing new BindIDs (gaps legal for definitely unpublished client IDs); Cancel for a greater-than-seen ID is violation because same FIFO publishing prohibits it overtaking Bind. Cancel for already terminal/older seen ID is bounded no-op, never key-wide revoke. Release compares captured fulltoken; no current registration lookup.

RequestAccepted correlation: zero/future-unallocated businessID is violation; a live pending call must be clientstream/duplex in AwaitingAcceptance, on exact Session, and exactly one transition wins with cancel/deadline. Duplicate ACK while liveAccepted is violation. For already terminal allocated IDs with no live pending entry, ACK is no-op: it cannot start producer or allocate resources. This uses monotonic nonreused existing request-ID high-water, not an unbounded perID tombstone dictionary. Session ID exhaustion seals new-call admission and begins bounded replacement/drain; admitted physical tails remain, no reuse. ClosedSession captured binding cannot dispatch.

Lock ordering: registry publication gate -> Session binding-table mutation -> global quota accounting, where needed. Release/disconnect first detach own Session records then release quotas without acquiring registry gate; post-cut invalidation outside registry gate. Active-call acquire uses exact cell's existing striped protection +closed state ordering, not a new global contended counter. Precise lock-free memory orders and physical-reader lifetime interactions still need code-level proof; this table does not replace them.

## Stable error allocation proposal

Existing top-level SharpLinkErrorCode values remain meaningful. Detail namespaces are scoped by top-level code; reserve new contiguous values only after collision verification against the integrated source inventory. Proposed Unavailable:100NoBoundRoute,101BindingUnknown,102BindingRetired,103SessionBindingDraining. FailedPrecondition:100ForeignLocalContract,101LocalContractUnavailable,102InvalidLocalContract,103MethodContractMismatch,104MethodShapeMismatch,105BindHashMismatch,106ReceiptScopeMismatch,107BelowMinimumVersion,108ExactRegistrationMismatch. NotFound:100UnknownBoundMethod,101UnknownBoundContract. ResourceExhausted:100BindingSlots,101BindingOperations,102BindingControlQueue,103BindingDemandSites,104BindingWaiters,105BindingMaintenance. ProtocolViolation continues existing MalformedFrame/ProtocolState reason mechanism with no attacker payload logging; no new top-level status needed. BindResult statuses 0..12 remain separate compact control status, mapped to these semantic details (RegistrationRetiring→Unavailable102; ScopeDenied→existingPermissionDenied+Unspecified; ControlDeadline→existingDeadlineExceeded+Unspecified; CancelledBeforeCommit→existingCancelled+Unspecified). No string-message oracle.

NoBoundRoute is local before business serialization/producer. Unknown/retired binding and unknown method can return typed pre-business error for response-bearing calls; invalid/retired plainOneWay produces bounded diagnostic, not success claim. OneWayStream requires bounded discard route/credit ownership, or closes Session if unsafe. Malformed flags/count/body grammar closes Session, not a business retryable error. Binding failure/rebind never grants non-idempotent replay. Explicit Prepare returns deterministic mismatch rather than waiting to hide it; replacement after success only invalidates future routes, never upgrades ExactRegistration.

## Request-ID exhaustion is an implementation obligation

An increment-and-skip-zero allocator alone does **not** establish nonreuse or safe high-water exhaustion. Protocol3 requires permanent exhaustion before issuing a nonpositive/wrapped ID, seal new-call admission and gracefully replace/drain that exact Session, and expose a coherent highest-issued-positive scalar for late ACK classification. Preserve the single ordinary increment on the steady path; do not add a per-call dictionary or a global lock. IDs1..Int64.MaxValue only; once exhausted no further allocation regardless counter value. In-flight allocator racers may return their already-issued positive IDs or fail, never return a negative/zero/reused positive. New tests pin boundary/max and concurrent exhaustion; this is a new correctness guard with cost measured later, not a claim current baseline already guarantees it.

Invalidation handling is governed by Bounded invalidation-before-result publication below: even unknown valid tokens advance a fixed per-slot watermark. Unknown-token no-op is NOT permitted for Invalidate. Old Release and retired operation-result correlation retain their separately defined no-op semantics.

# Identity width and graceful exhaustion decision

Choose one fixed **64-bit wire RequestId**, with positive issuance1..2^63-1, no variable-width/32-bit mode. BindOperationId uses the same positive 63-bit-bit domain separately. BindingToken uses full64 bits (slot16+incarnation48), never mixed with business IDs. This choice is not forced by the old15-byte header.

At one million *new calls* per second per physical Session, positive31-bit IDs exhaust in2,147.48s (~35.8min); a full unsigned32-bit domain in4,294.97s (~71.6min). At100k calls/s these are~5.97h/~11.93h. Positive63-bit space at1M new calls/s lasts~292,271years (365.25day years). These are arithmetic scale illustrations, not measured achievable throughput or a probability proof; correctness still enforces exhaustion.

32-bit IDs would save 4 bytes on every correlated frame, potentially~4MB/s at1M frames/s, and may matter for tiny streamed items. They would require frequent ID-generation/epoch plumbing or physical Session rotation under heavy call creation. Long-lived streams cannot migrate IDs, so rotation must preserve old Sessions/physical tails; under bounded pool/session budgets, repeated rotations can cause avoidable new-call unavailability. Adding an extra epoch field on every correlated frame largely loses the4byte saving; allowing reuse without it violates stale cancellation/response/ACK safety. Choose64-bit simplicity and much rarer rotation, while explicitly measuring its actual wire/CPU cost later. No claim of zero cost or perf equivalence.

A positive 63-bit-bit issuance subset simplifies a single monotonic ordered domain shared by pending-call tables and bounded late-ACK high-water comparisons, without using negative/sentinel IDs as valid calls. Using full unsigned64 would double an already enormous lifetime, not save wire bytes; it would require auditing every signed request/ordering use. This is a code/verification simplification, not preservation of successful old peers. Full-token64 remains distinct because slot/incarnation need all48upperbits. At1M reuse operations/s for one slot,48-bit incarnation space lasts~8.92years; atnormalcoldBind rates longer. No assumed rate prevents wrap: max is hard terminal admission refusal.

Header candidate 15 bytes is simply1magic+4length+1type+1flags+8ID, no wire padding. Compared to32bitID it costs 4 bytes; compared to padded16byteheader it saves1byte. Int32 length uses direct bounded BCL binary primitives and supports the existing explicit64MiB limit; no new3byte-length decoder or variable-ID branch is introduced merely to shave bytes. Packing/parser/JIT costs remain unmeasured. This is one concrete implementable model, not a performance acceptance result.

## No forced teardown solely to reuse an ID

At issuance/token-space exhaustion, permanently seal NEW call/Bind admission on that physical Session, withdraw it from new BoundTargets, notify bounded connection replacement, and let already-admitted pending responses, established streams, cancellation/credit and all physical cleanup tails finish. A new Session must handshake/Bind independently; no existing token/call/stream migrates. Existing request timers/budgets remain; no reset or implicit replay. Replacement respects pool/server/session caps: if no new Session fits, new calls fail typedUnavailable/ResourceExhausted, rather than violating cap or killing unrelated old streams. A caller-selected force stop may cancel through existing physical-ownership drain rules, not silently abort for rollover convenience.

Late ACK/results for old already-issued IDs remain correlated in the draining Session. In-progress Bind operations finish finalresult/release cleanup or ordinary protocol-fatal closure; successful late Bind cannot publish a new-call route after the session-admission seal. Module/producer reservations and linked cancellation sources remain until true physical completion (#931/#933); cleanup exceptions cannot skip obligations (#964). Separate protocol corruption or impossible required cleanup may still close the Session fail-closed; that is not ID reuse.

Required boundary tests: nearmaxbusinessID with concurrent allocators/long-lived serverstream+duplex; pending completion and delayedACK after seal; multiple exhaustedslots/Bindops with queued release; boundedreplacementcapacity refusal; no new business dispatch from a captured oldtarget after seal; unrelatedSession/site remains healthy; admitted oldphysicaltails complete and release exactly once. Do not emulate exhaustion by wrapping a narrow production ID for speed; test-only initial counter injection uses the real64bit path.

# Bounded invalidation-before-result publication

Choose the bounded watermark alternative, not a new promise that registry retirement waits for BindResult send ordering. A committed binding may retire and its Invalidate may be queued/received before its BindResult; receive and publication code must handle this. Existing required Bind-before-Cancel publishing order still applies to client-originated operations.

Each physical client Session allocates exactly negotiatedSlots entries of **u64 highestRetiredIncarnation**, initialized0, before it may become Ready. Index is the checked low 16 bits; value is high 48 bits of a structurally valid fulltoken. No wire-ID-based allocation, no unbounded tombstone dictionary. On any valid Invalidate(token), including an otherwise unknown token, store max(previous, incarnation) for that slot. Clear only matching/older live route or pending early-success eligibility; never remove a larger-incarnation newer route. A stale invalidation for incarnation2 cannot invalidate incarnation3. A future impossible token can at worst suppress this untrusted peer's Session routes; it cannot authorize anything. Out-of-range slot/zero incarnation/malformedreason is protocol violation. Watermarks live until that exact physical Session terminates, never copied to replacement Session.

Server must increment incarnation monotonically within each reused slot and never wrap, as already required. Every successful BindResult first passes complete structural, operation correlation, request identity/receipt and Session authority validation. Under the SAME BindingPublicationGate, advance the slot watermark to max(previous, returnedIncarnation-1) and remove only lower-incarnation target/buffer eligibility, then check returnedIncarnation > resulting watermark BEFORE buffering as early Publishing success, and AGAIN at route commit after enqueue confirmation. This advancement happens even when that correctly correlated result is cleanup-only due to local retirement/cancellation: it still proves that the server has reused the slot and older tokens cannot become new-call routes. Invalid/untrusted result identity does not advance state; it follows protocol failure. For incarnation1, incarnation-1 is0; no underflow or second array. Never decrement a watermark. The newly observed larger incarnation is not itself retired unless an invalidation/release already says so. An already buffered success invalidated before confirmation becomes cleanup-only; it cannot become Ready. Local release/eviction cleanup marks the same watermark so a late duplicate cannot revive a locally discarded target. ResultID correlation/key/receipt/generation/authority checks remain mandatory before this token eligibility decision. An invalidated success still finishes protocol ownership: queue exact Release if needed, wake/continue original bounded Prepare appropriately, then retire operation; it is never a request to bind latest registration.

Cold control publication synchronization: use a client-owned **BindingPublicationGate**, never on the steady RPC path. Under it, update watermark/local Session eligibility, validate result/key/localgeneration, and commit/remove the target in the site's immutable snapshot. Snapshot construction may occur outside the gate with a scalar publication version, but commit must revalidate watermark+Session/localgeneration+version inside the gate. Invalidation increments version and removes stale targets under that same gate, so an earlier checked-but-not-yet-committed snapshot cannot be published afterward. Do not await/user-callback/codec/selector policy callback while holding it; failed version commit returns to bounded coalesced control work, not a spinning unbounded rebuild loop. Local registry mutation may notify after its own gate/cut; publication never reacquires local registry while holding BindingPublicationGate. Invocation only Volatile.Read snapshot; a caller that captured an older route still faces server's exact retirement admission gate, not a client-side lease promise.

Allocation/aggregate bounds:8 bytes×negotiatedSlots =32KiB atdefault4096, max512KiB at65536 per Session, plus object/array headers not claimed measured. Add client aggregate Session tracking-slot quota default 65536/hard1048576, reserved before watermark/table allocation/handshakeReady and released only after physical Session control-state cleanup. Thus default watermark payload aggregate512KiB, hard8MiB, excluding other table/array overhead. Handshake/connection creation refuses ResourceExhausted when aggregate budget cannot fit, rather than weakening watermark or allocating beyond cap. This is cold memory cost, not per-RPC allocation proof. Stable ResourceExhausted detail106BindingSessionTracking is proposed, collision-checked with the same source review.

Required deterministic queue/barrier tests:
1. Server commits token then pauses BindResult enqueue; retirement closes group and delivers Invalidate first; resume BindResult. Client records unknown-token watermark, returns no Ready route, zero business dispatch/codec, exact cleanup.
2. Client Publishing buffers success before enqueue confirmation; deliver Invalidate, then confirm enqueue. No later snapshot publication, no producer start, exact one terminal cleanup.
3. Pause a prepared snapshot after validation but before publication; invalidate and advance publication version, then resume commit. Revalidation prevents stale publication.
4. DROP Invalidate2 entirely. Deliver fully correlated Result3, then delayed correlated Result2 while both operations otherwise remain valid. Result3 raises watermark to at least 2; Result2 cannot publish an old route or remove3. Repeat with Result3 buffered before enqueue confirmation and with a delayedInvalidate2; all orderings preserve3/cleanup-only correctness. Malformedout-of-range token allocates nothing.
5. Fill tracking-slot aggregate quota across many actual Sessions; next handshake fails boundedly, disconnect releases quota once, replacement gets a fresh zero watermark array, oldSession callback cannot mutate it.

These acceptance tests must target real Session/control-publisher/snapshot code, not a scalar substitute. This specification records no executed result or production-completion claim.

## Required acceptance controls for the real implementation

1. Golden new header, request57minimum, maximum-profile/auth varint boundaries, response71/135, Limits16/Authority48; fragmented every fixed-field boundary; exact end/trailing rejection.
2. Wrong0x89/#861 grammar, unknown magic/revision/type/flag/direction/correlation, oldApi5/mismatchedProtocol metadata reject before any generated factory/business codec; historical negative payload bytes pinned unchanged.
3. Capability required-subset, unknown-required/negotiated, compression/profile disagreement, duplicate/nonASCII profiles, auth bounds/authenticator ordering, missing binding authority and malicious negotiated-limit increase.
4. Every Bind/Result field bound/canonical zero/receipt requirement/status; exactServer/epoch/scope/hash/RegistrationId/ownGeneration. One immutable entry/token compare then acquire; same token number across two real Sessions cannot cross-route.
5. Actual normal-queue backpressure: BindPublishing early peer result, cancellation plus success, duplicate reply, unknown/future reply, late retired prefix, Cancel cannot overtake Bind; exactly one final result or Session close. No timer extension under activity.
6. Slot0/lastslot/out-of-range, incarnation1/max/overflow, old release/invalidation after reuse, captured entry before release vs SessionClose, creatorToken isolation across BindIDs. Server/client/global quotas atlimit/limit+1 release all counters under parse/allocation/enqueue faults.
7. BusinessID Int64.MaxValue concurrent exhaustion, no negative/zero/reuse, coherent high-water; ACKfuture/livewrongshape/duplicateAccepted/lateTerminal. Cancellation-beforeACK zero producer, ACK-beforecancel exactly one asynchronous producer start; cleanup owner joins physical completion.
8. Request16 vsOneWayStream18 across plain/compressed/metadata/deadline combinations, exact count mismatch, malformed varint/UTF8/truncatedprefix/negativebudget. Unknown binding/method plus throwing compression/codec yields zero business decode; valid registration decode uses same facts/protection through suspension.
9. OneWayStream invalid binding/method bounded discard across >one receive window, credit/route exhaustion close safely, no arbitrary wire-ID expansion. No one-way business success claim from local queue completion.
10. ProgressGoAway overtakes already-published Bind/Request: bounded typed refusal/discard, unrelated pre-admitted work finishes; no new registration admission. Stopping/Terminal blocks captured-entry business start.
11. Existing cancellation and resource rejection, metadata/compression policy-generation, interceptor retries and pending publication suppression retain original exact oracles; old-case failure is reported, never replaced by prototype-unimplemented expectation.
12. Longer Replace/reconnect/eviction runs prove bounded slots/ops/waiters/cache/maintenance and collectible payload detachment, not just lowperRPCB/op. Full five-family AOT/Trim/package/API/protocol/fullcanonical transport gates remain beyond staticUnary/OneWay slice.

Acceptance tests must compile against real source and use deterministic injected barriers/counters, not sleeps or changing quotas to force a favorable run. All source/SDK/package/UID/result archives follow exact immutable build identities. No compiler/test/native/timing run selected by this proposal. New numeric limits are not performance thresholds; no relaxation of old negative controls or adverse prototype history.


# Physical ownership and incomplete-feature boundary

All five families remain required: Unary, OneWay, ClientStreaming, ServerStreaming, and DuplexStreaming. A static Unary/OneWay source slice is not full #775 acceptance, and no narrower slice may be shipped under a claim that the other families are complete. ClientStreaming/Duplex producers require the exact RequestAccepted gate and original deadline; OneWay streams require bounded count-based discard/credit cleanup without a per-call acknowledgement. ServerStreaming needs no producer-start acknowledgement.

A successful binding is not a long-lived call lease. The server captures one immutable full-token entry, acquires that exact registration's protection, validates the same immutable method facts and request admission, then performs business decode/invocation. There is no mutable-registry relookup after asynchronous decode, queued dispatch, interception, or other suspension. Replacement closes the whole old registration group's new-admission gate before publishing the replacement. Captured old entries cannot retarget newer registrations.

Logical timeout, cancellation, response completion, or Session shutdown does not alone release physical decode, generated-code, iterator, producer, linked cancellation-source, module, send, or disposal ownership. Existing admitted tails retain the same registration/facts/protection until physical completion, including throwing cleanup. Failure of one cleanup callback cannot skip the remaining obligations. Control-state quota release, collectible payload detachment, and outstanding send ownership require their own terminal proof.

The independent physical-lifetime, producer-reservation and exception-safe cleanup obligations associated with public issues #931, #933, and #964 remain unaccepted until their real integration and deterministic controls establish them. Naming those obligations is not adoption of held changes or their performance results. Public API/ABI review (#805), complete-method-facts integration (#732), the real transport/five-family gates, NativeAOT/Trim/package/API checks, and bounded memory/ownership tests remain required. No benchmark, compiler, test, package, native build, merge, publication, or release result is asserted by this specification.
