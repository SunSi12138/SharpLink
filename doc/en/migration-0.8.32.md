# SharpLink 0.8.32 migration guide

Chinese: [`../migration-0.8.32.md`](../migration-0.8.32.md)

0.8.32 changes no public API, Protocol v2 framing, valid payload, or generated proxy/stub path. It tightens existing freeze and error boundaries and optimizes the common enabled-admission success path.

`ISharpLinkCompressionProvider.WireProfile` is validated and frozen when the Runtime Context is built. Client advertisement, Server selection, lookup, and session diagnostics use that snapshot. Mutating the property after Build no longer changes wire identity; the provider instance remains responsible for thread-safe, compatible algorithm behavior.

`SharpLinkAuthenticationResult.Reject` rejects undefined `SharpLinkErrorCode` values. A provider that bypasses the factory through the public constructor receives a stable `AuthenticationRejected` response. Any positive default request timeout remains valid and saturates at `DateTimeOffset.MaxValue` when necessary. Unix listener cleanup may preserve a stale path when identity capture failed because deleting an entry of unproven ownership is unsafe.

Admission rule behavior, queueing, fairness, and lease lifetime are unchanged. Immediate success for one concurrency limiter only removes internal transient arrays.
