# 3.0 contract-scoped RPC channel design

Status: selected API/architecture direction for #775; **not implemented or accepted as full #775**. This proposal intentionally permits breaking API and generated-ABI changes. It does not approve a performance regression, wire version allocation, release, or adoption of held ownership changes.

The existing extraction and deterministic scalar-model prototypes are separate evidence, not end-to-end binding/lifecycle acceptance. The goal is one coherent generated/manual path, with no legacy fallback or parallel compatibility mode.

## Recommendation: B, one identity-bearing contract-channel model

For the 3.0 breaking-API redesign, choose **B**, not A's client-wide raw methods plus configured default identity. The default-identity compatibility alternative's only remaining advantage is migration convenience; it adds an identity-selection state, an unconfigured-call failure mode, fallback-site allocation and a forwarding path without improving the binding model. Do not ship a default-identity compatibility mode, a legacy switch, optional ambient identity, auto-latest resolution, obsolete facades, or two execution pipelines.

B makes the **receiver** carry the complete local contract identity, scope and immutable registration requirement. Identity is supplied/validated once in synchronous local construction, not copied/hashed/looked up as a large argument on every RPC. Generated and handwritten callers use the same contract-scoped receiver and the same five-family pipeline. The public client is a lifecycle/control/proxy factory, not an identity-neutral invocation channel.

This is a breaking API/ABI change by design, not a promise to retain old raw casts. The old IRpcChannel type and client implementation are removed, not renamed-and-forwarded. A new IRpcContractChannel has no publicly implementable bypass into client internals: a third-party implementation represents its own channel, never a trusted SharpLink-owned capability. No remote BindingId setter or caller-supplied Session is exposed.

## Why this B rather than a per-call full identity parameter

Full identity belongs to the CallSite and its local generation, not every request. Passing it repeatedly invites mismatched channel/identity, dictionary dispatch and large state-machine captures. An opaque, client-issued local registration selector plus receiver eliminates missing identity by construction. A handwritten invocation may still fail for no bound route; it cannot fall back to guessed identity.

Retain RpcMethodDescriptor and typed codec/writer arguments initially because they carry real timeout/idempotency/shape/nullability and AOT-generic semantics, not to preserve signatures. Do not add a generic method-handle hierarchy or new per-method allocations merely to remove a scalar check. The receiver checks descriptor.ContractId against its frozen ContractId (and existing kind/flags/codec ownership validation) without a dictionary/hash scan. Same numeric ContractId never authorizes changing the receiver's hash/local generation/scope. Server Bind and per-request admission remain authoritative. A handwritten codec's claimed schema is a registration contract, not cryptographically proved semantics: malicious application code is not made safe by this API.

## Costs and capabilities

Remove one eager raw fallback site per client, all five raw forwarders and their interface adapters. Do not claim all interface dispatch or capture disappears: generated/manual calls still have their direct interface entry, CallSite retains its client owner, dynamic module/metadata wrappers still own leases, selectors and async/iterator state remain. Add one cold RpcProxy<T> preparation handle for generated creation (Service + exact channel), plus the required frozen scalar key/snapshot reference; cold bounded weak cache, local registration token, demand/snapshot arrays and waiter allocations are explicit. Any scalar method-contract check is real per-call work. No allocation-free/native/performance claim follows from this design.

Keep all five RPC families, explicit custom typed codecs and stream writers, request metadata, RuntimeContext access, cancellation/deadline, interceptor/retry/P2C/admission policy, collectible module ownership and cold preparation. RuntimeContext is a genuine public capability, so expose it on ISharpLinkClient and IRpcContractChannel rather than preserve a raw cast solely to access it.



---

# Concrete proposed public surface and owner rules

Names below are proposed exact source targets for API review, not already-existing APIs.

```csharp
public interface ISharpLinkClient
{
    IRpcRuntimeContext RuntimeContext { get; }
    // Existing lifecycle/registration/configuration functions remain.
    RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService;
    RpcLocalContract GetLocalContract<TContract>() where TContract : IService;
    RpcLocalContract GetLocalContract(System.Reflection.Assembly registeredAssembly, long contractId);
    IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null);
}
public sealed class RpcProxy<TContract> where TContract : IService
{
    // Internal constructor: caller cannot pair a Service with a different channel.
    public TContract Service { get; }
    public ValueTask PrepareAsync(CancellationToken cancellationToken = default);
}
public interface IRpcContractChannel
{
    IRpcRuntimeContext RuntimeContext { get; }
    ValueTask PrepareAsync(CancellationToken cancellationToken = default);
    // Five Invoke* members keep their complete current generic, in/ref, codec,
    // stream-writer, metadata, cancellation and return shapes; see the Five invocation members section below.
}
```

`RpcLocalContract` is sealed with an internal constructor. It is a client-issued, immutable selector for ONE validated local registration generation, never a remote lease or credential. GetLocalContract resolves current local registration only when explicitly called; an escaped token/channel never auto-upgrades to latest. Token fields are issuer identity, monotonically unique local registration-generation key, ContractId and full RpcAssemblyHash, all host scalars; no Type/Assembly/codec/stub strong fields. Tokens from another client, unknown/reclaimed registrations or retired local generations fail locally. Local registration keeps its existing module/codec payload until local lifetime rules permit removal. Resolving that payload occurs on channel construction under existing local protection, not each RPC.

Manual callers without a generic contract use the exact overload `GetLocalContract(Assembly registeredAssembly, long contractId)`. The assembly must be the exact object already accepted by RegisterAssembly/the validated generated manifest registry. Under local registry protection, resolve only that assembly's currently registered generation and contract descriptor; reject absent/retired/not-owned/ambiguous contracts, never search another owner's latest matching ID. An explicit new lookup after re-registration can intentionally select the newly registered generation of that exact assembly, but all escaped tokens/channels remain frozen. No new property is added to the shared server/client registration-result type. Custom/manual generated manifests retain the existing codec/descriptor/hash ownership validation. Assembly/Type is a cold input only and is not retained in the returned scalar token or control cache. Caller-held assembly/manifest references are application ownership, not framework unload guarantees.

```csharp
var local = client.GetLocalContract<IMyContract>();
var channel = client.CreateContractChannel(local, new RpcProxyOptions { Requirement = requirement });
await channel.PrepareAsync(cancellationToken); // caller controls timeout via linked token
var answer = await channel.InvokeUnaryAsync(method, in request, requestCodec,
    responseCodec, metadata: null, cancellationToken);
// No ((IRpcChannel)client), no SetDefaultIdentity, no BindId/Session supplied by app.
```

Generated callers use the same exact-site preparation without casting or resolving current identity again:

```csharp
var rpc = client.CreateProxy<IMyContract>(new RpcProxyOptions { Requirement = requirement });
await rpc.PrepareAsync(cancellationToken);
var answer = await rpc.Service.MyMethodAsync(request, cancellationToken);
```

The immutable sealed RpcProxy<T> holds the generated Service and precisely its channel/wrapper. Its internal-only constructor prevents mismatched pairing. It is one cold object per returned proxy handle (not per call); it retains the same local generation/scope/requirement for its lifetime. Accessing Service and invoking its generated method does not pass through RpcProxy. Remove old Get/GetWithMetadata, not an obsolete alias: all generated creation returns the preparable handle. Fixed metadata is captured via RpcProxyOptions.Metadata and the existing metadata wrapper. Request metadata semantics are not removed.

Both CreateProxy and CreateContractChannel atomically register bounded local demand before returning; demand creation is local and never waits for Connect/Manifest/Bind. Existing/new Ready Session/topology/Manifest/receipt events may schedule bounded coalesced Bind operations. On factory failure/limit rejection no partial escaped demand is left. A live receiver retains its demand; controller/cache tracking is weak or explicitly released on local retirement/client stop, with hard live-demand/cache/waiter quotas. Prepare adds only its bounded waiter and triggers/pulses existing demand, never installs a second identity. Thus a call may become ready without Prepare, while immediate calls still FailFast until actual confirmation. Before-Connect, after-Connect and receipt handles all prepare THAT original site, even if registry replacement occurs while waiting. Retired local generation fails; no reacquisition of latest.

Prepare is optional and bounded; it waits for the channel's exact requirement. Calls default FailFast/NoBoundRoute until a confirmed route exists, without serialization/producer start. Prepare does not itself reserve call capacity, prolong registration lifetime, or allow old local generation calls. Cancellation of one waiter does not cancel unrelated coalesced demand; hard waiter/operation quotas and original deadlines remain. Already-ready Prepare returns synchronously after local lifetime/state validation; it must not create a fresh Bind operation. A separate deterministic scalar model does not establish this production behavior.

Generated CreateProxy creates the same local token/key internally and passes the direct ContractCallSite as IRpcContractChannel to the generated proxy factory. Static channels may be the CallSite itself; dynamic channels retain the existing module-owned wrapper, which obtains local invocation protection before touching collectible codecs. Metadata wrappers delegate to the same site and retain immutable metadata without weakening identity. The RpcProxy handle is never captured as an extra invocation receiver; generated Service stores its direct channel. All-five async entry/core/interceptor/retry/telemetry/send pipeline stays owned once by the CallSite; no new forwarding async layer.

CallSite/cache/control plane retain only issuer/client, scalar local identity+scope+requirement and immutable snapshots of host-scalar BoundTargets. A live proxy/manual dynamic channel may legitimately retain its local codec/module owner; framework weak caches, pending demand/task completion and retired snapshots must not create permanent collectible roots. Channel creation cannot acquire a remote long lease. Weak cache identity must survive local registration snapshot clones; cache eviction only removes cache ownership, not mutate escaped site identity. Hard bounds include live demand sites/waiters as well as cache entries.

Local construction precedence: null/invalid option/token inputs -> client terminal state -> issuer/exact local generation validation -> local descriptor/hash/codec ownership -> immutable key/cache/wrapper creation. It does not consult Connect, Manifest or Bind. Invocation preserves reviewed codec/null and cancellation/deadline/control ordering except the explicit added local identity/retirement validation; exact relative ordering across these guards must be source-frozen and tested before implementation, never inferred from a proposed API. NoBindRoute is after local validation and original budget checks, before payload/producer work. Server error ordering remains framing -> captured token entry -> exact lifecycle protection -> method facts/shape -> request admission -> business work.

## Persistent async-decode / physical completion ownership

After exact server entry capture and successful registration protection, the resolved call carries the SAME registration payload, immutable RpcMethodFacts instance and module/call protection through queued dispatch, persistent asynchronous decode, generated stub/interceptor continuations and all producer/iterator/disposal tails. No continuation re-resolves mutable registry state or swaps to a newer registration/facts/codec. Logical response/cancellation/timeout may end caller visibility, not physical cleanup ownership. Cleanup detaches old payload only after every physical generated-code/decode/producer task and disposal obligation exits; failure of one cleanup callback cannot skip the others. Required #931/#933/#964 integration is not implemented by this API or model. Deterministic delayed-decode cancellation/replacement/throwing-cleanup controls must prove this independently.

## Frozen construction and demand outcomes

Factory errors have no network fallback: invalid/null inputs or structurally invalid options -> ArgumentException/ArgumentNullException; terminal client -> existing disposed/stopping result; foreign selector -> FailedPrecondition/ForeignLocalContract; absent/ambiguous/retired exact local registration -> FailedPrecondition/LocalContractUnavailable; invalid local hash/codec ownership -> FailedPrecondition/InvalidLocalContract; exceeded site/demand/waiter quota -> ResourceExhausted. These are proposed stable detail enums, not string-message matching. Foreign/retired handles cannot succeed by choosing another registration. A concurrent retirement either wins before local owner acquisition (factory fails) or follows a successful protected factory linearization (returned handle remains exact and its next Prepare/call fails local retirement); creation never promises future availability.

Factory demand reservation uses the complete validated scalar key and participates in the same local lifetime check; roll back newly reserved demand/cache slots on wrapper/factory construction failure. Controller publication must be nonthrowing after irreversible reservation commit. Active bounded Bind operations/waiters may retain a scalar site/demand until final protocol cleanup, but must clear on completion/disconnect; they do not hold module payload/codec/Type. Weak controller tracking alone is not permission to grow tombstones: sweeps are bounded, dead keys recycled, and overload rejects before allocation beyond limits. Exact capacities remain the real protocol/source gate, not arbitrary new numbers in this API choice.

RpcProxyOptions is captured immutably at construction: target scope, full original receipt/registration requirement, and metadata snapshot. Mutating a caller-owned option object/metadata buffer after factory return must not alter an escaped handle. The options implementation must either be immutable or copy mutable inputs once; that cold copy allocation is counted. Old `Get<T>()` migrates to `CreateProxy<T>().Service`; old GetWithMetadata migrates to the same factory with Metadata set. Callers needing explicit readiness keep the RpcProxy handle and call its PrepareAsync; no runtime cast or proxy-to-site dictionary is introduced.


---

# Scope of the next reviewed implementation and validation changes

## API/ABI and generated schema

1. Remove IRpcChannel and SharpLinkClient : IRpcChannel, `_rawCallSite`, five public raw facades and raw-cast adapters. Change proxy factories, metadata/module wrappers, generator emission, test manifests and API/AOT fixtures to IRpcContractChannel. Do not keep an obsolete alias or two paths. RuntimeContext consumers use the client property or explicit channel.
2. Add scalar opaque RpcLocalContract, explicit local-token acquisition, CreateContractChannel and Prepare; CreateProxy<T> returning RpcProxy<T> local-only construction semantics, with Get/GetWithMetadata removed. Preserve five method shapes/codec/writer arguments recorded in [Five invocation members retained for semantic/generic capability](#five-invocation-members-retained-for-semanticgeneric-capability). Generated ABI identity and public API baseline change atomically; exact version/name allocation needs separate inventory review, not assume existing Api5 label can silently accept old factory binaries. C0 complete-method-facts ABI changes must be reconciled rather than overwritten.
3. Existing RpcMethodDescriptor is local invocation metadata, not a wire identity or authority token. Never serialize its ContractId as a BindingId. Channel owns full identity; BoundTarget owns exact Session reference/generation+BindingId; server entry freezes one registration. Shared mutable 'current identity' and per-request full identity dictionaries are prohibited.
4. Breaking API does NOT relax wire boundary: independently negotiate one new binding grammar; old ContractId16-byte and prior36-byte identity grammar rejected before business work. Bind contains full hash/receipt once; ordinary route prefix remains BindingId64+MethodId64. OneWayStream count extension and RequestAccepted are retained. This API decision does not allocate frame numbers or numeric protocol limits. Independently freeze real protocol limits, IDs, flags, compression and rejection grammar before production; scalar-model constants are not protocol allocation authority.

## Full775 obligations unchanged

Normal routes retain Manifest-derived eligibility; receipt can actively Bind only a verified same server/epoch despite stale Manifest. Compatible/MinimumVersion/ExactRegistration use the selected registration's own publication generation and exact RegistrationId; no auto-latest downgrade. Stable scoped site snapshot coherently publishes topology/routes/exclusions, uses existing selector generation/P2C and reserves the exact physical Session without carrying token through redirect.

Server gate closes entire old registration group before replacement publication; prepare all fallible resources before irreversible cut. Binding has no call lease. One immutable slot entry is captured, full token compared, then that entry's protection acquired; never token-check then reread. No reuse on incarnation wrap. Retirement detaches collectible payload after physical readers/calls/streams finish. Bounded operations, waiter counts, invalidation state, quota and cancellation/Session teardown are mandatory. Bind/Cancel ordering uses same bounded publication queue; preinstall Publishing correlation before exposing bytes; fast early reply cannot publish a route before enqueue confirmation; late cancel cleanup and graceful Draining Bind rejection remain required. Different published Bind IDs never share creator token.

ClientStreaming/Duplex producers wait per-call RequestAccepted under original deadline; Bind is not resource admission. OneWay remains no per-call ACK, and OneWay with streams has bounded independently parseable count/discard/credit cleanup. Held931 physical tails,933 producer reservation and964 exception-safe frame cleanup are separate integration obligations, not satisfied by this API or MODEL. C0 scalar facts must stay one resolved instance through exact registration dispatch.

## Concrete tests to replace/add, not delete to get green

- Replace E0 raw-cast/facade ownership assertions with public API absence plus direct generated and handwritten scoped-channel ownership checks; preserve every five-family null/codec/cancel/deadline/interceptor/metadata test's intended semantics. Old tests remain historical evidence, not silently reinterpret old pass counts.
- Construct before Connect/with absent or stale Manifest; identity local only; no factory network or business serialization. RuntimeContext public access works without a cast. Generated RpcProxy.PrepareAsync before/after Connect and with receipts must bind the exact same channel/generation/options as Service; replacement between construction and Prepare cannot resolve latest. Cold demand can become ready without Prepare; ready Prepare creates no new Bind.
- Wrong issuer, exact generation retired/replaced, empty/mismatched local hash/codec owner, arbitrary invalid descriptor and method-contract mismatch fail before business codec; same numeric ContractId in another hash/generation/tenant cannot substitute receiver identity. Manual registered manifest works for all five families and custom codecs under AOT/Trim.
- Concurrent same-key creation reuses one stable live site (losing cold CAS allocations counted), distinct receipt/scope/local generation never shares site; registry cloning does not lose escaped-site updates. Weak cache/task/snapshot retention and bounded quotas tested with collectible ALC, while user-held proxy ownership is disclosed.
- Already-ready Prepare does not issue Bind; first call FailFast until ready, exact deterministic mismatch delivered, individual waiter cancellation does not cancel others; no infinite wait on stop/drain/local retirement.
- Real Session Bind, group retirement, full token ABA, fast-peer publication race, in-flight Bind vs GoAway, stale result/invalidation/cancel and bounded reclamation retain all35 MODEL scenario obligations plus actual deterministic concurrent/transport proof.
- All five real families, RequestAccepted failure/timeout/cancel vs producer start; no-ACK OneWayStream rejection/discard; C0 facts + held ownership cleanup races, including persistent async-decode held across replacement/cancellation: original registration/facts/protection survive until actual decode and physical terminal cleanup, with no mutable-registry relookup. Canonical multi-transport validation is required; unavailable transport tests must remain explicitly pending, not counted as full-suite success.
- Update API/ABI/package/AOT/generated factories and wrong-generation rejection; exact discovery source/UID delta before full collectors; old35 MODEL results do not count as new API runtime evidence.

## Smallest implementation gate after design approval

First freeze the local API/ABI migration and source owner/error-order map on an actual integrated source tree: replace the raw receiver with explicit scoped receiver and generated/manual factory inputs, without claiming binding readiness. Do not ship a functional legacy-contract-wire implementation behind new binding API names. The first executable feature slice must join local creation to real bounded Session Bind and exact token dispatch for static Unary/OneWay, with other families still explicitly unimplemented only inside the nonshipping prototype. No final feature removal. Then group lifecycle/receipt and complete streaming/physical ownership integration follow separate exact reviews. No broad performance campaign is requested; later targeted same-host mechanism/cost checks must test this design's actual direct route, not old missing-body probes.


## Five invocation members retained for semantic/generic capability

```csharp
ValueTask<TResponse> InvokeUnaryAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default);

ValueTask InvokeOneWayAsync<TRequest, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;

ValueTask<TResponse> InvokeClientStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;

IAsyncEnumerable<TResponse> InvokeServerStreamingAsync<TRequest, TResponse>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default);

IAsyncEnumerable<TResponse> InvokeDuplexStreamingAsync<TRequest, TResponse, TStreams>(
        RpcMethodDescriptor method,
        in TRequest request,
        IRpcCodec<TRequest> requestCodec,
        IRpcCodec<TResponse> responseCodec,
        in TStreams streams,
        SharpLinkMetadata? metadata,
        CancellationToken cancellationToken = default)
        where TStreams : struct, IRpcClientStreamWriter;
```

## Related obligations and implementation order

Track full acceptance in [#775](https://github.com/SunSi12138/SharpLink/issues/775), public API/ABI review in [#805](https://github.com/SunSi12138/SharpLink/issues/805), and complete method-facts composition in [#732](https://github.com/SunSi12138/SharpLink/issues/732). Physical lifetime, outgoing producer reservation and exception-safe send cleanup remain separate obligations represented by #931, #933 and #964; naming them does not adopt those branches or their performance results.

Before production changes, freeze the actual-base symbol/caller migration, combined generated-ABI identity, exact five-family guard/error precedence and bounded static Unary/OneWay real-transport slice. Then validate group retirement/receipt/ABA and complete streaming/resource ownership, package/AOT/Trim and full canonical suites. Targeted mechanism/cost evidence must account for cold handles, weak caches, retained owners, scalar guards and actual direct route; no missing-body inference or new favorable threshold.
