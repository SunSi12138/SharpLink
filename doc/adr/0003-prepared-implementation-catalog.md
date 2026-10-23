# ADR 0003: Prepared implementation-only server catalog transactions

- Status: Proposed
- Date: 2026-10-22
- Related: #929, cooperative same-session maintenance

## Context

Ordinary `ReplaceAssemblyAsync` deliberately publishes the replacement before completing
old-module drainage. Its public result and cancellation behavior are not a distributed
quiescence certificate. Reusing it after a coordinator has retired the old modules would
also violate its requirement for a running old registration.

The first cooperative operation keeps client contracts and codecs immutable. It needs a
separate cold transaction, not per-RPC epochs, registration checks, or a second routing
identity. In particular, matching the implementation assembly's hash is not proof that its
contract owner or referenced codec generation is compatible.

## Decision

The internal `PreparedImplementationReplacement` accepts an explicit nonempty set of exact
old/new implementation assembly pairs. Every replaced manifest must own services but no
Contracts, global Codecs, or ContractCodecs. Each pair preserves its complete route set and
exact contract `Type`. Contract ownership, nonempty actual owning RPC hashes, and the exact
bound generated dependency closure are checked. Dependencies outside the replaced set stay
on the same assembly objects; retained dependants of replaced implementations and dependency
cycles are rejected. This intentionally does not enable general client/server catalog replacement.

Metadata preparation reserves one server-registry cold writer and captures its exact module
objects, service snapshot, registry revision, runtime revision, and immutable dependency owner
hashes. Ordinary register, replace, and unregister writers cannot publish through the lease;
manifest broadcasts use the same boundary. The configured maintenance mode further reserves
ordinary mutation for the host coordinator. Neither mechanism participates in ordinary dispatch.

Only a framework coordinator's `QuiescenceProof`, bound to this server and exact plan, permits
candidate construction and retirement. A local prepared plan, zero module count, Boolean,
or application callback is not a substitute. The coordinator must establish the full session,
reader, producer, admission, and physical-work cut before invoking the operation.

After that proof, candidate stubs and codec bindings are prepared while paused, before any old
gate is retired. The whole target service/module snapshot and runtime adoption maps are
preallocated. Implementation-only scope leaves the context-global codec graph unchanged, so
existing immutable contract providers need no live-global rebinding.

The sequence is: validate exact base; permanently seal every replaced module; await module
owners and all old connection/singleton service disposal in dependent-first order; dispose old
implementation codec owners; commit prebuilt state while paused. Commit rechecks lifecycle,
registry identities/revision and runtime revision under the registry boundary. After validation,
publication performs no user callback, allocation, manifest scheduling, or fallible map insertion.
The coordinator retains the writer until its separate resume barrier completes.

A preparation failure can release the unsealed writer without changing old admission. Any
post-seal failure retains a closed writer and never reopens an old gate or calls the outcome
rollback. Stop changes lifecycle before waiting, joins a started physical-disposal owner, cleans
candidate resources, and prevents a later commit. Terminal retained plan handles drop old
implementation roots, including completed/faulted physical-operation Tasks; module release and
collectible ALC collection are tested separately.

## Consequences

- The public register/replace/unregister contracts outside configured maintenance remain separate.
- Replacing an assembly that also owns a contract or codec is explicitly unsupported in this mode.
- Local transaction success is only `CommittedPaused`, not remote quiescence, resume, or #775
  ExactRegistration. The coordinator owns all network/session claims.
- Cold manifest and codec-provider readers are serialized at publication. There is no new normal
  dispatcher check or codec `Get` fast-path field. Immutable external providers remain usable.
- Uncooperative user execution or async disposal can keep physical release pending. A timeout or
  observer cancellation does not make disposal complete or authorize reopening.
- Arbitrary application calls into escaped old service/provider instances are not framework work
  certified by this primitive.

## Alternatives considered

- Moving the old gate earlier in ordinary replacement: still permits partial/fallible publication
  and confuses its existing rollback behavior with permanent retirement.
- Preparing a general replacement codec graph against live global providers: can silently bind
  old dependencies and would require a broader isolated resolver/catalog design.
- Per-invocation version checks: violates this operation's cold-control/unchanged hot-path scope.

## Validation

`PreparedImplementationReplacementTests` loads actual generated service-only assemblies into
collectible contexts. Its local stage tests explicitly bypass the coordinator proof via reflection
and do not claim a transport or cohort certificate. They cover writer exclusion, stale base,
equal-hash/different contract generation rejection, dependency mismatch, codec-binding failure,
retired connection async disposal, permanent gates, disposal failure, Stop races, prepared runtime
adoption, and ALC release while retaining successful or failed terminal plans. Integrated coordinator tests remain
required for any same-session success claim. Standard guards follow [PR Fast](../pr-fast.md).
