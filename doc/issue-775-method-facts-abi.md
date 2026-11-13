# #775 complete method facts and receiver ABI

Base: 7d51a8254ee90711fdad7c7b93296cdc02e37bbb, the accepted private group/receipt slice.
Production dev remains 160d0cd9a0550de5bf00c63b6007376c02debc20. This is the next #805
implementation delta, not final package or release approval.

## Reuse the existing scalar descriptor

Keep RpcMethodDescriptor as the one immutable facts value. Its existing byte flags have
room for SupportsCancellation without increasing the32-byte layout. Retain every existing
field: contract/method ID, five RPC kinds, client-stream count0–127, response payload and
nullability, timeout-declaration presence separately from nullable timeout value/full ticks,
and idempotency. No Type, Stub, Assembly, Codec or delegate is stored in the value.

In the private profile, IRpcStub requires TryResolveMethod(id, out descriptor); no default
implementation, separate SupportsCancellation query or fabricated Unary fallback remains.
Unknown returns false; invalid known facts fail cold binding validation. The generated
manifest carries the same complete scalar value, and Bind compares the complete resolver
result before publishing an entry. All four Stub invocation methods receive in descriptor
instead of a bare MethodId. The final generated business switch uses descriptor.MethodId.

The outbound generated bridge also receives the complete descriptor, by value because its
implementation may await. Stream exception mapping uses this original identity, not a
lookup of the latest registry. This completes the ABI contract before enabling streaming
transport admission. Existing standalone serialization and RuntimeContext APIs are retained.
Admission selectors retain their existing identity/kind projection; the retained queue
already owns the full bound value. No new public selector property is added just for tests.

MagicBA / ABIsharplink-experimental-775-method-facts-v4 isolate this incompatible experiment.
The wire body is unchanged from the accepted receipt slice. Final production version/API
allocation and actual-package signoff remain with #805.

The exact production-declaration delta inside this profile is:

| Declaration | Decision and consumer impact |
|---|---|
| RpcMethodDescriptor.SupportsCancellation | Add one public init-only flag in the existing scalar value; no new public type or layout growth. |
| SharpLinkGeneratedMethodDescriptor.Facts | Add one public init-only scalar property. Regenerated manifests must supply the complete value; old/default facts cannot pass registration. |
| IRpcStub | Replace the two optional narrow queries with required TryResolveMethod. Replace the MethodId parameter of four invocation signatures with in descriptor. All generated and handwritten Stub consumers change together. |
| IRpcGeneratedServerBridge.PumpOutboundStreamAsync | Replace payload-nullability/contract/method parameters with the full value. Keep typed stream/Codec, request/stream IDs and cancellation. |
| Experimental Magic / AbiIdentity | Change together to BA / method-facts-v4; API integer5 alone remains insufficient. No production version is assigned. |

No additional helper, new wrapper type, selector property or inheritance opening is public.
The existing RuntimeContext/standalone serialization, transport/SPI and metadata receiver
contracts are retained. This is an implementer review of the exact delta; final independent
source/package review is still required by #805.

## Preserve error ordering as each family is integrated

The ordinary product is unchanged. Its static inner-entry order is:

| Family | Existing synchronous entry / deferred boundary | Insertion when scoped streaming is enabled |
|---|---|---|
| Unary | request/response Codec null checks; caller cancellation; capture one interceptor/control generation | local identity/shape after Codec checks, then existing cancellation/control order; bound target before serialization |
| OneWay | request Codec null check; caller cancellation; capture/control, no client default timeout | same insertion; no producer before method/target admission |
| ClientStreaming | request/response Codec null checks; caller cancellation; capture/control | same insertion; RequestAccepted before producer, retain original logical deadline |
| ServerStreaming | capture interceptor/control at invocation; Codec/progress checks in Resolved; dynamic module lease at enumeration | do not move capture to enumeration or move deferred retirement to factory; validate the scoped receiver at the corresponding boundary |
| DuplexStreaming | same capture/deferred split, with independent producer module protection | preserve both consumer and producer physical owners and error timing |

The existing module-owned wrapper adds distinct earlier boundaries: Unary/OneWay acquire
the module before the inner Codec/cancellation checks; ClientStreaming obtains separate
call and producer protection before those checks. ServerStreaming/Duplex capture policy
and the original control/deadline at invocation, then check logical progress and acquire
module protection on first enumeration before calling Resolved. Dynamic local integration
must preserve these observed precedence and synchronous-fault/deferred-enumeration differences,
not apply the static table as a uniform replacement. It remains unsupported in this profile.

Current private Unary/OneWay already use the scoped insertion above. The three streaming
receivers remain explicitly unsupported until per-call admission/physical ownership is
implemented; their current failure timing is tested, not represented as completed streaming
integration. A facts ABI change alone must not enable producers or claim five-family runtime
acceptance. Dynamic local-generation registration remains a later coupled change.

## Verification and stop boundary

All RPC, lifecycle, generated runtime execution and performance are GitHub-only; local work
is development, basic builds and source checks. Keep the accepted99/67/49 exact inventories
and budgets. Add a separate frozen facts/ABI inventory before its first hosted run, covering
all five generated shapes, scalar preservation/layout, unknown/invalid facts, external
consumer signatures, wrong-ABI preflight, mapper/interceptor/admission propagation, and no
business work or producer from unsupported streaming receivers. New direct generated Stub
controls also run only on GitHub, not locally.

An ABI mixup, repeat resolver call after binding, lost fact, wrong mapper identity, physical
ownership/ALC failure or changed original error ordering stops that subject. Preserve failed
evidence and fix its cause before another subject. No performance experiment is scheduled.

The new runtime inventory is **49 facts/ABI cases**, fixed in
eng/775-slice/facts-expected-cases.json before the first Action. A separate exact negative
consumer build must report CS0535 for the missing resolver and readonly invocation ABI;
an unrelated build failure cannot satisfy that control. The positive handwritten consumer
wraps the actual generated Stub and deliberately throws on any resolution after real Bind.

## Execution record

Local basic builds caught a fixture assumption that admission selectors exposed Method;
their established API exposes only contract/method/kind. The fixture now observes that
projection and the retained queue's eventual full Stub value, without expanding the public
API. Profile Probe and previous-ABI fixture builds pass with zero warnings/errors. Existing
admission source controls pass5/5; the active-source guard permits exactly one cold server
resolver and no narrow/fallback method queries.

Tested subject **81ff7ab7d5f5a9e5a27aa7c8bfb13beff2c7bd4c** passed its first hosted run:
[43417677630](https://github.com/SunSi12138/SharpLink/actions/runs/43417677630).

| Host / .NET10.0.15 | TCP | SharedMemory | Job |
|---|---|---|---|
| Windows x64 | 99 +67 +receipt49 +facts49 | 99 +67 +receipt49 +facts49 | 131859584693 |
| Linux x64 | 99 +67 +receipt49 +facts49 | 99 +67 +receipt49 +facts49 | 131859585066 |

All16 JSON reports passed exact inventory/source/kind/transport/uniqueness checks, both in
the Action and offline. Both hosts also passed the exact CS0535 old-Stub negative consumer
and wrong-ABI metadata-preflight-before-construction controls. Default/profile/plugin basic
builds passed; the active-source audit permits one resolver only in cold Bind, and the real
RPC wrapper throws if resolution is attempted after binding. Admission/queue, telemetry,
exception mapper with and without interceptors, and terminal Stub controls all passed.

The54 changed/shared production files retain identical ordinary-compilation tokens and
build inputs relative to2360a0ca. Therefore the original ordinary Unit result is retained,
including Windows2211/2212 with the unresolved #387 Stop failure (Linux2212/2212). No ordinary
suite was rerun or relabeled. No performance data was produced.

**Decision: accept this bounded facts/ABI delta and proceed to per-call streaming admission
and physical ownership.** Generated Stub/bridge controls cover all five shapes, but the
three streaming transport receivers still reject before producer/enumeration. The next
slice must connect RequestAccepted and retain the original registration/facts/Codec and
producer/consumer protection through their real physical exits. Full package/API inventory,
independent #805 signoff, dynamic local generations, final canonical regression and valid
performance acceptance remain outstanding; #775/#805 stay open, no PR/merge.

Development source remains in C:/Users/sunsi/Desktop/SharpLink-775-Slice on local
codex/775-static-bind-slice. Complete source bundle, original ZIPs/job logs, exact reports,
workflow snapshot, issue handoff and restoration README are retained under
C:/Users/sunsi/Desktop/SharpLink-775-evidence/facts-20261113/.
After capture, remove only this stage's temporary CI branch/workflow; historical runs remain.
The final handoff changes only documentation/workflow removal after the tested subject.
