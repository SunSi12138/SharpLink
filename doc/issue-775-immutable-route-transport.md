# Issue #775: bounded dual-identity TCP experiment

## Status and intended decision

This verification branch is an experiment, not a releasable prepared-proxy API.
It follows the negative same-shape substitution milestone in
`issue-775-immutable-route-probe.md`. The baseline is dev commit
`13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`, not the older Binding prototype.

The candidate retains logical `ContractId` and adds an internal eight-byte wire
route field to `RpcMethodDescriptor`. Its layout is 40 bytes rather than 32,
including for ordinary calls. This deliberately violates the existing 32-byte
layout guard: the guard is retained, not weakened to make this experiment green.
There is no claim that eight extra bytes are free, or that this is the originally
proposed same-shape substitution.

## What changes on the hot path

- Client sender arguments read `WireRouteId` instead of `ContractId`. The same
  16-byte request prefix carries route and method; there is no additional wire
  field, wrapper, lookup, route lease, or per-call scope synchronization.
- Logical descriptors still reach endpoint admission policy, interceptors and
  telemetry. Logical record equality/hash ignore the internal wire identity.
- Server dispatch substitutes a route-keyed frozen registration snapshot for the
  existing contract-keyed snapshot, with the same single dictionary lookup.
  Logical registration storage remains separate for cold publication/manifest work.
- Existing module admission/lease protection remains. The old module's admission
  gate is closed monotonically before replacement dispatch publication; the gate
  is never reopened. Queued redispatch reuses its immutable wire token, not the
  logical contract key that could now name a replacement.

## Cold control and explicit boundaries

Internal test-only opt-in must occur before server start/client connect. Generated
`Get<T>()` access is rejected for an experimental client. Tests use handwritten
stubs and direct descriptor calls through real SharpLink TCP transport, not a new
public generated prepared factory.

The experimental manifest appends a registry scope GUID and exact logical-to-route
mapping. Ordinary manifests retain their existing bytes. This extension is not
capability-negotiated and must not be shipped as cross-version compatibility.
A fresh registry scope belongs to one server object. Route IDs are positive,
monotonic, never reused, and exhaustion fails rather than wrapping. Failed cold
preparation may burn IDs without changing the published snapshot.

Client handshake validates the pinned registry scope before sessions enter Ready
pools, including reconnect/refresh paths. Preparation verifies the full assembly
hash and agreement of ready session manifests, then returns a descriptor snapshot.
It observes a manifest, not a server-linearized Prepare receipt: the registration
may already have retired, in which case calling it must fail, not retarget.

The descriptor must remain with the originating client/handwritten proxy. It does
not carry or enforce client ownership. Moving it to another independently scoped
client with a colliding numeric route is unsupported and not made safe by this
experiment. Registry GUID matching is not a replacement for existing transport
authentication or a demonstrated tenant/security-domain migration protocol.

## Lifetime and unsupported semantics

The tests exercise real module admission and service disposal after a held call.
A generated descriptor read before module acquisition returns constants and retains
its stub reference; no demonstrated use-after-free is claimed. Conversely, a zero
module call count is not proof that all stale readers or collectible assemblies
are gone. Eventual collectible ALC unload is not established by these tests.

Retired Unary routes fail. Retired OneWay routes retain the baseline unknown-route
session-termination behavior: collateral failure of other work on that session is
possible. Local OneWay send acceptance is not server acceptance or a FIFO fence.

Only Unary and OneWay without client streams are prepared. Strict producer-start
acceptance, streaming, generated prepared proxies, NativeAOT, shared-memory
transport, cross-server migration and production capability negotiation are outside
this bounded candidate. Production security/lifetime defaults are not relaxed.

## Correctness evidence

Results are recorded below after execution. Tests in `ImmutableRouteTransportTests`
are real TCP/client/server tests with dynamically emitted manifests and handwritten
stubs. They are distinct from the explicitly sequential toy model in the earlier
probe. `ImmutableRouteManifestTests` exercises codec/identity invariants directly.

## Performance evidence

`eng/immutable-route-performance` compiles identical harness/helper sources against
baseline and candidate, on the same SDK/runtime/configuration. See
`route-performance-harness.md` for commands and measurement definitions. It does
not benchmark a generated prepared proxy or establish equivalence from a
statistically inconclusive result. Whole-process allocation includes server and
client; OneWay service counters are not a proof of completed final lease cleanup.

### Representation alternatives and retained state

A separate route argument would require another generated/internal invocation ABI
and thread that scalar through entrypoints. The bounded candidate instead preserves
entrypoint signatures and changes their value-type layout. This is still an ABI
experiment, not binary compatibility.

An alternative could keep the 32-byte descriptor route-valued and reconstruct
logical metadata only inside existing optional callback gates. Bare calls could
avoid the reconstruction lookup, but enabled telemetry/interceptors/admission/retry
would need a new route-to-logical map lookup. That map must retain old identities
until outstanding callbacks finish, not just mirror the current manifest. This
alternative is not disproven; it exchanges descriptor footprint for callback lookup
and retention machinery and was not implemented here.

Static inspection distinguishes field footprint from heap allocations:

- Bare Unary unpacks the descriptor into existing `StartUnaryCall` scalar arguments;
  `PendingCall` does not gain a descriptor. Wider value argument copies are possible;
  an eight-byte allocation increase per Unary is not established.
- Plain OneWay and retry async state machines retain a descriptor, whose field grows
  eight bytes. Suspension and rounded async-box layout determine actual allocation.
- `AttemptOutcomeState` retains one descriptor; interceptor invocation state plus
  public invocation context retain two collectively; telemetry `CallScope` retains
  one. Each stored descriptor grows eight bytes, not necessarily each allocated
  object's rounded size by exactly eight.
- Nullable descriptor selection arguments also widen. Source inspection cannot
  determine which copies the JIT eliminates.

## Local verification (2026-10-21)

SDK10.0.102, Microsoft.NETCore.App10.0.2, Debian13 x64. Release builds use
`-m:1 -p:BuildInParallel=false -p:UseSharedCompilation=false` because this cloud
sandbox disallows MSBuild worker named-pipe IPC. No security settings were changed.

- UnitTests and both same-source performance harness builds: zero warnings/errors.
- `ImmutableRoute*`:17/17 passed (7 TCP,6 actual registry,4 codec/identity).
- Existing `DynamicModuleTests`:5/5; `ProtocolV2ContractManifestTests`:2/2;
  `ContractManifestReadyBoundaryTests`:1/1; `SharpLinkClientContractManifestTests`:5/5;
  `SharpLinkServerInvocationTests`:27/27; `SharpLinkServerRequestScopeTests`:5/5;
  `SharpLinkServerRequestScopeReviewTests`:6/6.
- Existing `RpcMethodDescriptorTests`:3/4 pass. The retained layout assertion fails
  explicitly, expected32/found40. The candidate is not a green production test suite.
- Total selected cases:71 passed,1 expected layout failure; no skipped cases.
- Project-reference guard:9 projects/13 edges passed.
- Maintainability debt gate:1253 files passed,17 historical oversized files remain
  within baseline allowances. This does not establish public ABI compatibility.

Run selected cases after building:

```sh
dotnet test/SharpLink.UnitTests/bin/Release/net10.0/SharpLink.UnitTests.dll \
  --treenode-filter '/*/*/ImmutableRoute*/*'
# Replace class selector with each existing class listed above for regressions.
```

Fixture assemblies are noncollectible Reflection.Emit assemblies, because default
manifest type resolution cannot return a collectible assembly. AssemblyResolve
handlers are removed at fixture disposal. These tests prove service/module disposal
ordering, not collectible ALC reclamation. The full unit/integration suite was not
run; generated prepared-proxy, NativeAOT and shared-memory checks remain not run.

### Observed TCP comparison, not a release gate

Four ABBA blocks produced8 baseline and8 prepared-candidate process samples, plus
2 candidate-ordinary diagnostics. Each process warmed10,000 Unary/OneWay pairs,
then measured10,000 serial Unary and100,000 OneWay calls. Tiered compilation and
PGO were both explicitly enabled. Source and binary hashes were identical before
and after the run set. No other builds/tests ran concurrently.

| Metric | Baseline median [min,max] | Prepared candidate median [min,max] |
|---|---:|---:|
| Unary ops/s |9536 [7635,11561]|9055 [6268,11270]|
| Unary p50 µs |69.394 [56.021,83.307]|66.489 [46.577,77.623]|
| Unary p95 µs |239.486 [213.020,314.237]|277.293 [227.552,329.746]|
| Unary p99 µs |569.512 [433.421,1074.504]|645.481 [505.704,1672.462]|
| Unary process allocation B/op |392.123 [391.969,392.174]|392.141 [392.094,392.186]|
| OneWay service counter ops/s |722468 [471241,1174548]|732245 [509064,935607]|
| OneWay process allocation B/execution |207.839 [177.031,401.149]|208.134 [185.386,275.084]|

Unary median throughput is5.0% lower and median run p95/p99 are15.8%/13.3%
higher, but broad cloud process variance prevents attributing these percentages
solely to the route change. The candidate-ordinary diagnostics also have broad
ranges and only2 samples, so cannot isolate descriptor size causally.

OneWay local sender acceptance median increased41%, while observed service
execution throughput increased only1.35% and ranges overlap heavily. Local
acceptance is explicitly not server throughput or strict producer acceptance.
No statistical equivalence analysis, controlled-machine acceptance threshold,
zero-allocation or zero-overhead conclusion follows from these data.

Raw per-process JSON, source/binary/runtime manifest, descriptive summary and
selected test logs are checked in under
`eng/immutable-route-performance/evidence/20261021/`. The recorded source snapshot
predates adding these evidence files/docs/workflow; production binaries were not
rebuilt or changed during timing. Logs include the incompatible layout failure.

### Decision supported so far

The route substitution inside the existing dispatch lookup is feasible in this
bounded TCP experiment and preserves tested old-registration behavior. The larger
descriptor is not a free implementation: it breaks the existing layout contract
and increases retained/copy footprint, including ordinary calls. These findings
do not justify merging this branch or claiming “control semantics with completely
unaffected hot performance.” A production API/generator integration and a chosen
cost budget would be separate design decisions, not completed by these results.

### JIT inspection independently confirms changed hot code

Separate diagnostic processes used `DOTNET_TieredCompilation=0` (FullOpts),
not the timing configuration. ReadyToRun was left at the environment default. In actual
`InvokeUnaryCoreAsync<RpcEmptyRequest,int>`, native code grew952→972 bytes.
The actual `InvokeOneWayPlainCoreAsync<RpcEmptyRequest>` state-machine `MoveNext`
grew2480→2531 bytes. Principal stack reservations remained264 and480 bytes,
respectively. The Unary no-observer path performs an additional eight-byte
value copy when forwarding the widened descriptor to `GetReadyConnection`.
Optional observer paths also copy the wider value.

Native code size alone is not a throughput metric. It does, however, directly
refute “unchanged hot instructions” for this concrete candidate, independently
of the noisy process timing. Extracted complete method disassemblies and
identifying summaries are in the evidence `jit/` directory.

Reflection plus `Unsafe.SizeOf<T>()` on the real closed generic OneWay state-machine
type measures192→200 managed bytes, while the descriptor measures32→40 bytes.
This is state-machine storage, not a claim about rounded boxed allocation per call.
The scratch inspection source is archived as `.txt` beside its JSON output to avoid
accidentally compiling it into the performance harness.

**Stop this candidate's expansion.** The extra hot descriptor copy is concrete and
violates the requested structurally unaffected hot path. One-lookup registration
routing remains technically demonstrated; all future representations/new ABIs are
not disproven. No third candidate is implemented, and no merge/release is proposed.
