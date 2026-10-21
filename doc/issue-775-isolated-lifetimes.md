# Lifecycle-isolated registration domains: feasibility evidence

Baseline: `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb`. Production `src/` is unchanged.
This is a test-only feasibility investigation, not a new production feature or an
implementation of every original #775 guarantee. Runtime results are pending CI
at the initial evidence commit; inspect the exact-commit workflow before treating
any case as passed.

## Contract under test

A domain owns a fixed server registry, client/pool, and acquired proxies. Prepare
and authenticate a new domain before publishing it for new business sessions or
batches. Existing proxy holders remain on their old client. Switch acquisition at
that boundary, not through an extra per-RPC picker. Do not replace registrations
inside a domain or transparently retarget its connections.

The test harness uses real loopback TCP and handwritten manifests/stubs with
noncollectible emitted assemblies. It does not test generated proxy creation,
collectible ALC unloading, NativeAOT, shared memory, or long-running streams.
Identical logical contract/hash values are deliberately used in both domains.

## Eight executable checks

1. Two distinct server/client domains coexist: old client remains on old service,
   new acquisition reaches new service, and Unary/OneWay work with 32-byte ABI.
2. New-domain authentication failure leaves the established old session usable.
   The authenticator models bootstrap expiry; no real credentials are used.
3. Counterexample: an old client reconnects to a new server on the same reused TCP
   endpoint and invokes the new registration under baseline behavior.
4. An entered old call can finish during graceful shutdown while the new domain
   serves requests independently.
5. Module retirement timeout explicitly returns `ReferencesReleased=false` while
   a noncooperative call holds service ownership; eventual release is separate.
6. Received-but-queued admission work is rejected during Stop, not guaranteed to
   execute successfully. Only already-admitted service work is being drained.
7. Counterexample: zero-grace Stop can return while old user code and deferred
   disposal remain incomplete. A completed Stop is not a full reclamation barrier.
8. Existing handshake authentication hooks can reject an old expected-domain tag
   when the TCP endpoint is reused by a different domain, before it becomes Ready.

The domain tags in test 8 are synthetic nonsecret values fixed by each server,
with the old client retaining its expected old value. They are NOT credentials,
NOT proof of server authenticity, and NOT a general protocol implementation.
Production would need authoritative nonreused domain IDs obtained from a trusted
control plane, domain-specific validation on the server, and composition with
real authentication and transport server authentication. A malicious peer that
simply accepts every tag is outside this test's claim. Tagging cannot solve lost
or expired bootstrap credentials; a fresh connection may still be impossible.

## Source-backed boundaries

- Per-client and per-server object ownership: `SharpLinkClient.cs:10-35,73-101`,
  `SharpLinkServer.cs:17-40,68-101`. Static generated proxies capture their original
  client at `SharpLinkClient.Lifecycle.cs:328-342`.
- GoAway schedules reconnect: `SharpLinkClient.Lifecycle.cs:607-622` and
  `SharpLinkClient.RpcChannel.cs:309-330`. Baseline compatibility checks compare
  contract hashes, not registry lifetime: `SharpLinkClient.ContractBinding.cs:115-143`.
- Server Stop closes admission and dynamic gates, then may defer service cleanup:
  `ServerLifecycleCoordinator.cs:362-406`. It can publish Stopped afterward
  (`474-480`). An existing characterization test asserts this behavior:
  `ServerLifecycleOwnershipCharacterizationTests.cs:258-301`.
- Calls-drained checks provisional admission and active call counts:
  `ServerLifecycleCoordinator.cs:567-594`. Pending decode retains old session,
  payload, service and request permit: `SharpLinkServer.PersistentDecodeDispatch.cs:71-158`.
- Queue shutdown cancels waiters rather than promising business execution:
  `Admission/AdmissionStateKernel.cs:596-647`.
- Connection close schedules service cleanup instead of joining it:
  `ServerConnectionState.cs:381-405`; retired connection ownership is retained by
  `SharpLinkServer.ConnectionLifecycle.cs:260-332`.
- Client Stop does not await every caller-owned logical invocation:
  `SharpLinkClient.cs:160-243,389-391`. Held interceptors/enumerators can remain.
- Stream route removal/count zero is not consumer/codec reference release:
  `StreamManager.cs:389-405,1131-1156` and
  `PooledAsyncStreamDispatcher.cs:1343-1405`.

## Decision boundary

This direction can preserve the production invocation code and descriptor layout
by moving acquisition and connection-scope checks to cold boundaries. It is not a
claim of no system-level cost: overlapping pools, instances, buffers, credentials,
capacity and resource contention remain real costs.

Do not recommend general automatic reclamation from `StopAsync` alone. A cold
retirement completion must join existing server calls, framework/decode tasks,
deferred global and connection service cleanup, plus application-owned logical
operations and disposed stream consumers. Timeout must remain a distinct outcome;
retain old resources until that completion, rather than freeing them optimistically.
Some completion signals currently exist only internally; merely assembling public
Stop calls does not expose a full safe completion contract.

This is not equivalent to preserving old proxy behavior on a shared connection
or avoiding reauthentication from #760/#775. Those requirements are explicit
tradeoffs requiring a product/API decision, not waived by passing these tests.

## Reproduction

Use repository-pinned .NET SDK 10.0.102. Build the UnitTests project in Release,
then run with `--treenode-filter '/*/*/IsolatedLifecycleTests/*'`. Run the original
`RpcMethodDescriptorTests` filter as a separate unchanged ABI check.
`git diff --exit-code 13928a35 -- src` must pass.

Local build is supported. Local runtime execution was blocked before test cases:
the default sandbox denied test runner IPC sockets, and the permitted escalation
failed in sandbox setup. No protected paths or permissions were changed. The
branch-only CI workflow runs the real TCP tests in GitHub's runner instead.

## Initial CI finding retained

Run [39599746128](https://github.com/SunSi12138/SharpLink/actions/runs/39599746128)
ran eight cases: seven passed and module-timeout teardown failed. A background
ContractManifest broadcast checked a Ready connection, then attempted its send
after session phase became Draining. The resulting protocol exception surfaced
through framework-task drain during server Stop. Production source is unchanged;
this is an observed baseline shutdown/publication race, not proof every Stop fails.

The follow-up fixture corrects the module timeout's expected RPC outcome to
Unavailable and waits for the already-scheduled manifest publication to finish
before teardown, using a test-only internal observation. This isolates the module
resource assertion; it does not fix or erase the observed race and is not a public
retirement API. Reconnect cases additionally wait for old Ready count to reach zero
before binding the replacement listener, avoiding stale-Ready test observation.
