# #805: contract-binding API delta

## Candidate decision

Keep the contract-scoped receiver and immutable publication results. Remove the old
raw channel and `Get` factories rather than retaining an identity-free path around
binding. This is an intentional 3.0 breaking change: regenerate contracts and migrate
custom channel/Stub implementations together. It is not a 2.x compatibility shim.

This is the implementer's review of source
`1a64f67d1ffd6fd94f0a1b4dc3252770d5e06e6d` against dev
`160d0cd9a0550de5bf00c63b6007376c02debc20`. Release/API approval belongs to the PR
review; neither the historical #805 inventory nor this baseline update supplies it.

## Exact scope and evidence

The existing Roslyn runner rebuilt all ten production projects on Windows with
SDK10.0.115/runtime10.0.15. Both inventories have complete target coverage. Their
selection is every externally visible declaration plus every non-sealed class,
including compiler-implicit members and accessors: 3397 baseline, 3474 candidate;
99 additions, 22 removals and six same-ID shape changes. All selected changes are
in Abstractions. The other nine projects' selected declarations are unchanged.
No new inheritable class is introduced.

Local evidence under `artifacts/775-integration/`:

- `api-inventory-base160/` and `api-inventory-1a64f67d/`: compiler inputs, source
  locations, attributes, nullability, generic constraints and declaration relations.
- `api-delta-1a64f67d.json`: complete before/after declaration rows.
- `binding-api-review-1a64f67d.json`: explicit IDs and the decisions below, including
  synthetic record/accessor members. The decision set covers this delta only.
- `published-api-1a64f67d/`: actual published2.0.3 and candidate3.0.0 package DLL
  APIs, forwarding metadata and package/assembly provenance. Nine candidate packages
  carry the candidate source commit. Eight published packages exist; GenerationControl
  is an added package, not a missing historical inventory.

The candidate package APIs differ from the previous committed3.0 baseline only in
Abstractions. All20 SDK ExportedType forwards retain their target assembly identity.
The updated3.0 declaration baseline locks this proposed surface, not its release approval.
Archived baselines, the old inventory decisions and the published-package evidence
remain unchanged.

## Decisions by owning declaration

| Declaration group | Decision and reason |
| --- | --- |
| `IRpcContractChannel` and its five typed invocations, runtime context and finite `PrepareAsync` | Keep public generated-code SPI. Each built-in instance captures exactly one local contract generation and binding scope. `in` requests and struct writers retain typed Codec paths. External implementers implement their own channel; the interface alone grants no built-in binding authority. Preparation has a required finite timeout, with cancellation affecting only that wait. No raw-channel alias or default successful preparation. |
| `ISharpLinkClient` local factories/runtime context and multi-cluster `CreateProxy` | Keep public. Factory work is local; the returned handle prepares the same receiver used by `Service`. Opaque local selectors reject a foreign client or retired generation. Multi-cluster routing captures one child at creation and never re-routes an existing proxy. Null options mean Compatible with no receipt/metadata. Required members deliberately require custom-client migration. |
| `RpcLocalContract` and `RpcProxy<T>` | Keep sealed with internal constructors. The selector contains host scalars, not Type/Assembly/Codec roots; callers cannot mint a built-in registration identity. The handle exposes read-only Service and forwards finite preparation. `T : IService` is preserved. |
| `RpcProxyOptions` | Keep sealed and publicly constructible with init-only nullable Metadata/Receipt and Requirement. Default enum value is Compatible. Invalid/foreign receipt scopes fail during local construction, without remote business work. Metadata is the existing immutable metadata type, not a mutable caller dictionary. |
| `RpcRegistrationReceipt` and `RpcRegistrationRequirement` | Keep public scalar readonly record struct and three-value enum. Synthesized equality, deconstruction, init accessors and default construction are intentional value semantics. A default or forged receipt is not authority and fails scope validation. Server instance/epoch and contract-owner hash constrain every receipt; MinimumVersion uses that registration's publication generation, ExactRegistration also its registration ID. No module lease or collectible owner is stored. |
| `IRpcStub` resolution and four invocations | Replace optional descriptor lookup and separate cancellation lookup with required `TryResolveMethod(out RpcMethodDescriptor)`. False means unknown; there is no guessed/default successful method. Four invocations consume the same readonly `in` descriptor resolved at admission, not another method-ID lookup. Existing bare-ID signatures and `SupportsCancellation(methodId)` are removed. Custom Stubs must migrate; package negatives enforce the exact interface obligation. |
| `RpcMethodDescriptor.SupportsCancellation` and generated method `Facts` | Keep init-only fields on existing value/manifest declarations. The descriptor's ten-argument constructor and both Deconstruct contracts remain. Timeout presence and raw ticks preserve null, zero and negative semantics. Generated manifest, Proxy and Stub carry the same facts; resolver controls cover five families, rejection ordering and no wrong-Codec execution. |
| `IRpcGeneratedServerBridge.PumpOutboundStreamAsync` | Replace separate nullability/contract/method scalars with the original descriptor, passed by value to the async streaming boundary. This preserves the original facts through physical stream exit; no lookup against a later registration. Inbound stream SPI is unchanged. |
| Generated contract descriptor ProxyFactory, constructor and Deconstruct | Replace only the factory channel type with `IRpcContractChannel`; provider/Stub factory contracts remain. Regeneration is mandatory, not an implicit delegate adapter. |
| `ISharpLinkServer` receipt getter and two publication methods | Keep public server-only control-plane API. Getter is exact-owner current-state observation. Register/Replace methods return receipts captured under the original publication gate before old-work drain, never a post-operation query. New receipt-bearing default methods explicitly reject unsupported implementations without calling their legacy registry. The required getter is a deliberate custom-server migration. Existing registration API/result contracts are unchanged. |
| Two server publication result classes | Keep sealed with internal constructors and read-only original outcome/receipt-list properties. Lists are immutable, ContractId-ordered and empty for rejection/contract-only publication. The result contains no module/Assembly/Codec roots. Held R1 with R2 then R3 returns R2's original result; retained receipts permit actual ALC collection. |
| Six frame enum values and four version constants | Keep frame IDs17–22, generated API6/exact `sharplink-3.0-api6-contract-binding-v1`, protocol minor/floor5. Cold Bind, per-call two-way RequestAccepted and bounded OneWay input are real protocol changes. Old API5 and wire4 must fail closed, not be accepted by an integer-only check. Existing normal magic is retained. |
| Old raw `IRpcChannel` and four `Get`/`GetWithMetadata` factories | Remove. An identity-free fallback would violate the chosen receiver contract and undermine guard ordering. Migrate to retained `CreateProxy`, options and finite preparation; migration examples and package compile negatives show the new path. |

## Acceptance still attached to this change

The source-bound full Integration502 and binding513-per-transport controls pass;
Unit continuation2221 passes with the separately paused/red #387 excluded. These
are correctness evidence, not performance acceptance. Final packaged consumer,
old real generated-binary rejection, hosted platform/Trim/NativeAOT and startup/
steady short-stream performance results remain separate PR acceptance obligations.
The historical NativeAOT public dynamic-registration limitation is not redefined
as supported by measuring an internal module boundary.
