# Issue #775: immutable-route feasibility gate

## Verdict and scope

**Stop the naive same-shape `ContractId = RouteId` substitution.** It changes the
logical contract seen by valid existing client endpoint-admission policies. This is
an executable counterexample to that specific candidate, not a proof that every
32-byte representation, cold-context design, or new generated ABI is impossible.

The isolated executable in `eng/immutable-route-probe` does not change production
code or defaults. It references the real `SharpLink.Abstractions` project, tests
its public `RpcMethodDescriptor` and `ISharpLinkEndpointAdmissionPolicy`, and
contains a separately labeled, sequential route protocol model. It does **not**
run SharpLink transport, generated proxies, concurrency races, reconnect or ALC
unload tests. An exit code of zero means the evidence checks reproduced the
counterexample; it does **not** mean the candidate passed its semantic gate.

## Pinned baseline and reproduction

The production baseline is `13928a35df4bff3cd99b14bd1df131aa3eb3bcdb` (dev), not
the old #862 Binding prototype. Use the repository-pinned SDK, 10.0.102. No
third-party package is introduced by the probe.

```sh
python3 eng/immutable-route-probe/run.py > immutable-route-evidence.json
python3 eng/check-project-reference-boundaries.py
```

`run.py` refuses changed or untracked production source files before building. It
records the actual SDK, runtime, architecture, checkout HEAD, and dirty-tree flag.
If the environment has a read-only home, set `DOTNET_CLI_HOME` and `NUGET_PACKAGES`
to writable local cache directories; do not change the user's home permissions.
The executable alone reports a reference baseline, not a verified source match.

## What the executable establishes

1. An existing public endpoint policy that accepts logical contract `101` accepts
   the original Unary and OneWay descriptors. Substituting wire route `303` in
   `ContractId` makes the same policy reject them, although the intended 16-byte
   request prefix now contains the desired route. This is a deliberately small
   policy counterexample, not an end-to-end client test.
2. The baseline descriptor is 32 bytes on the tested x64 runtime. The obvious
   `{ RpcMethodDescriptor LogicalMethod; long RouteId; }` alternative retains both
   meanings but is 40 bytes. This is a layout measurement only: no throughput,
   latency, allocation or JIT-regression percentage follows from it.
3. The sequential model demonstrates why a never-reused route can resolve only
   its original registration or fail after retirement/removal, and why a reused
   logical key can resolve a delayed old request to a replacement. The delayed
   and queued labels are the same token lookup at different modeled times, not
   proof of actual queue implementation coverage.
4. Additional model checks illustrate monotonic retirement with held leases,
   scope rejection before Ready publication, and rejecting identity exhaustion
   rather than wrapping. These are required invariants, not implementations of
   the production lease, pool, connection refresh, or allocator.

## Why an existing cold context does not repair the simple substitution

At this baseline:

- `RpcGenerator.ProxyEmitter.cs:129` emits a static shared descriptor per method.
- `IRpcChannel.InvokeUnaryAsync` / `InvokeOneWayAsync` receives that descriptor,
  not a proxy/registration identity. A `SharpLinkClient` serves multiple contracts;
  it has no existing per-proxy logical-identity field at this boundary.
- `SharpLinkClient.Invokers.cs:396,616` forwards `method.ContractId` to the wire
  send. `SharpLinkClient.InvokerSend.cs:53-54` writes the two route prefix fields.
- `SharpLinkClient.Interceptors.cs:94-95` copies the descriptor into the public
  invocation context; `SharpLinkClient.Attempts.cs:59` passes it to a user endpoint
  admission policy. `SharpLinkTelemetry.cs:165,461` uses its logical contract ID.

The server side is different: its generated stub already provides the logical
method descriptor. A route-keyed dispatch map can preserve one dictionary lookup
while keeping logical registration bookkeeping separate. Queue redispatch would
look up the same immutable route token rather than the latest logical contract.
That is still a candidate design, not implemented by this model.

Possible repairs each constitute another explicit hypothesis: a larger descriptor,
a changed reference-based ABI, a lookup on optional callback paths, or a specialized
per-contract channel. A larger descriptor is not automatically slower; optional
path lookup need not tax the no-policy path. Conversely none is evidence for
“all existing semantics with an unchanged hot path.” A single-contract client or
disabled callbacks must not silently substitute for the existing general contract.

## Lifetime and reconnect boundaries remain open

Do not characterize the generated method-descriptor read before the module lease
as a demonstrated use-after-free: it returns value constants, and a retained stub
reference roots its collectible code. A module call count of zero nevertheless
does not prove all stale readers vanished or an ALC was collected. Production
tests must separately establish non-entry after retirement, disposal after final
service use, and eventual collectible unload.

Prepared routes require cold verification of server identity, registry epoch and
security scope before every new or refreshed session enters the prepared pool.
The real `ClientConnection.SessionRefreshRedirect` path and all pool publication
paths remain untested here. An old proxy must never be automatically rebound.

Removed OneWay routes and session-wide failure behavior, policy updates, prepare
failure/rollback, resource limits, and stream setup acceptance also remain open.
Strict “server accepted before producer starts” is not solved by a cold Prepare.

## Stop condition

The accepted experiment required correctness before performance. The simplest
same-shape substitution fails an existing observable client contract, so do not
expand it into a production feature or claim an RPC performance result. Integrated
correctness, TCP/shared-memory throughput, tail latency, allocation and JIT-path
comparison against dev are all **not run** for a candidate implementation.

Keep this negative gate as evidence. Continuing with a changed ABI or an explicitly
narrower semantic/performance goal is a new design choice, not a green result for
the original substitution.
