# ADR 0003: Local proxies, indexed routes and server contract admission

- Status: Proposed
- Date: 2026-10-13
- Related: [#775](https://github.com/SunSi12138/SharpLink/issues/775), [RPC wire identity](../rpc-wire-identity.md)

## Context

A control connection can complete dynamic replacement while a data connection still holds the previous manifest. Synchronous proxy creation cannot establish remote availability, and a client-side manifest check can become stale before arrival. The abandoned [#802](https://github.com/SunSi12138/SharpLink/pull/802) reported a substantial OneWay sender regression; that observation does not establish that manifest checking caused it. The new implementation starts from current dev and retains existing allocation, queue and performance gates.

## Decision

Proxy creation freezes local generated contract/codec ownership, module generation, target client and options. Control-plane events publish immutable indexed candidate sets. Each attempt applies the existing Picker policy within its eligible set and reserves the selected session without redirecting to an unselected connection. The initial policy is FailFast; explicit observation waits remain separate and no implicit wait queue is introduced.

Every Request carries an uncompressed contract/hash and bounded invocation shape before business payload. Server admission validates against the current registration and acquires that exact registration's lifecycle reference atomically with respect to publication. The same registration supplies codec, service and stream lifetime. The client route snapshot is never authoritative for dispatch safety.

Receipts contain scalar server instance, registry epoch, publication generation, contract/hash and registration identity. Successful handshakes bind the remote server and epoch. An explicit receipt can select that same server despite a stale manifest, within the proxy's target scope; the server checks compatibility and the requested minimum or exact registration. No global Type cache or remote proxy lease is introduced.

Two-way client/duplex streams wait for `RequestAccepted` before starting producers. The ACK follows identity/version/resource admission and receive-route reservation, using the existing pending call's terminal/cancellation/deadline lifecycle. OneWay has no ACK and retains queued-completion semantics. Its wire stream count permits bounded discard on pre-business rejection.

## Alternatives considered

- Checking manifests in `Get`: blocks valid local work, races with replacement and cannot order independent connections.
- Scanning every manifest before every RPC: duplicates control-plane work without closing the server-side race.
- Prioritizing manifest broadcasts: improves convergence but provides no registration lease or cross-connection order.
- Starting all client streams immediately: runs application producers before identity rejection is known. Two-way establishment acknowledgement makes the required boundary observable; OneWay retains its distinct contract.
- Hidden readiness waiting: adds queueing and lifetime policy to every call. A future explicit bounded wait can be reviewed independently of the correctness mechanism.

## Consequences

Wire minor 5 rejects older grammar at negotiation and adds 20 bytes to the ordinary identity prefix plus 56 bytes for an explicit receipt. Successful handshake responses carry server and registry GUIDs. Two-way client/duplex establishment adds one control frame and can wait for it; subsequent stream elements retain their route. Generated API/ABI identity is unchanged; public proxy/receipt additions require reviewed package API baselines.

Receipt identity follows the trust of the authenticated transport/handshake. It is not a new authentication scheme, authorization token, global generation or exactly-once protocol. Different server instances and epochs cannot share a receipt timeline. Already admitted operations can finish under their old registration; later operations can be rejected after replacement.

Ordinary calls add an indexed lookup and server pre-decode check. Demonstrate their costs with [matched performance evidence](../issue-775-performance.md), reporting OneWay sender queue completion separately from receiver completion. Keep canonical gates unchanged and distinguish protocol establishment costs from unexpected regressions. Do not explain a regression by naming a suspected component without attribution evidence.

## Validation

Use local/protocol/admission unit tests, independent real TCP connections with a blocked manifest, all five RPC shapes, same-hash and changed-hash replacement, server/epoch isolation, cancellation/deadline/stop, drain and collectible ALC tests. The exact reviewed head must also pass [PR Fast](../pr-fast.md), full generator/integration suites, transport/NativeAOT/trim/package smoke, generated ABI and wire compatibility guards, and matched performance. Source and test boundaries remain subject to the existing maintainability policy.
