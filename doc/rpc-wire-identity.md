# RPC wire identity and contract binding

A generated ContractId/MethodId is a compact lookup identifier, not a compatibility proof.
The generated contract owner's deterministic 128-bit RpcAssemblyHash identifies its canonical
RPC wire surface. Bind requires an exact, nonempty local hash; there is no fuzzy per-method fallback.

## Construct locally, prepare on the real data Session

`CreateProxy<T>()` and `CreateContractChannel(client.GetLocalContract<T>())` freeze one validated
local registration generation, its hash, scope and immutable requirement. Both are synchronous
local operations; neither consults a remote Manifest or performs a network round trip. Retain
the returned handle/receiver. [Prepare it explicitly](rpc-channel-3.0-migration.md) with a finite
budget when the next call requires readiness.

After handshake, each server Session publishes a ContractManifest: ServerInstanceId, RegistryEpoch,
registry generation, and ContractId/hash entries. Connection Ready means that bootstrap completed,
not that every receiver has a confirmed binding. A missing entry keeps normal preparation pending;
an advertised incompatible hash is a definite FailedPrecondition. Without a bound route, business
calls fail fast with Unavailable/NoBoundRoute before payload/producer work.

The controller coalesces matching demands and sends bounded BindContract operations on the actual
data Session. The server validates full identity and optional receipt, then installs an immutable
binding for **that exact service registration** before replying. Bind does not activate a service,
run a business Codec, reserve a call, or freeze future authorization decisions.

## Keep token, Session and registration together

The confirmed call-site snapshot publishes topology, compatible candidates, retry exclusions and
Session/token pairs atomically. The existing Picker selects and reserves that physical Session.
The ordinary 16-byte route prefix is BindingId + MethodId, not ContractId + MethodId; the full
hash/receipt is not copied or revalidated on every business call.

The server still checks the captured full token, acquires its registration's protection, and
validates method facts/shape before resource admission and business decoding. Subsequent queue,
decode, interceptor, Stub, producer and consumer cleanup keep that same registration/facts/Codec.
A new Session cannot borrow a predecessor's token, even if it reports the same Server/epoch.

## Replace and reclaim without retargeting

Replacement closes the whole old group's admission gate before publishing its successor. A
pre-cut protected call may complete on the old registration; new work cannot reacquire it after
the cut. A binding never changes its meaning to point at latest. The controller may prepare new
bindings for later attempts under the original scope, while ExactRegistration must not downgrade.

Invalidation can arrive before a delayed Ack; that Ack cannot resurrect a known-retired route.
Slots, operation correlations, invalidation state and quotas are bounded. Reclaimed slots change
the full token incarnation, and retirement detaches collectible payload only after physical owners
exit. Idle bindings are not call leases and must not permanently root old Assembly/Type/Codec data.

## Receipt identifies one publication, not remote authority

Server `RegisterAssemblyWithReceipts` and `ReplaceAssemblyWithReceiptsAsync` return immutable
per-service receipts captured at their own publication. The receipt contains the Contract owner's
hash but the Service's registration ID and publication generation. A later replacement while old
work drains cannot change the returned result. `GetRegistrationReceipt` is only an exact-owner
current-state snapshot, not an operation result.

Compatible checks identity and an explicit receipt's Server/epoch. MinimumVersion also checks
that service's own generation; ExactRegistration additionally checks the registration ID. A
receipt-aware Bind may target a verified same-Server/epoch data Session with a stale Manifest,
but still requires the real BindAck. Receipt and BindingId grant no authorization and reserve no
capacity. Full grammar and limits are in [Protocol v2](protocol-v2.md).
