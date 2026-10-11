# ADR 0002: Contract-aware call admission in 3.0

Status: Proposed

## Context

Issue #775 separates local proxy construction from remote readiness. A held proxy can outlive a server registration, and manifests are observations rather than authorization to decode a request. Contract hashes alone cannot distinguish same-hash replacements. Compatibility must hold for the actual reserved session, including planned blue/green redirection.

## Decision

Generated proxies bind immutable local scalar requirements through internal channel entry points. RpcMethodDescriptor remains unmanaged and unchanged. The built-in client's unbound IRpcChannel entries reject calls rather than adopting a current registry hash for an independently held codec. This is an intentional 3.0 behavior break.

The 3.0 wire requires ContractAdmission negotiation and protocol minor/floor 5. Old peers fail closed. Request routing is followed by a versioned identity envelope (hash, envelope version, match discriminator, optional server/epoch/generation/registration constraint). The request-only identity flag shares its numeric bit with the response-only truncation flag; readers validate by frame kind. Compression starts after the complete variable prefix. Manifest v1 publishes scalar timeline and registration identities. Unknown envelope versions fail closed.

Admission filters within the existing topology and endpoint selector, then reserves and revalidates the actual connection used by send. WaitForCompatible uses a shared bounded queue and change signals captured before snapshots. The logical deadline is frozen before interceptors, waits, retries, or delayed enumeration. No request serialization or stream producer begins while admission waits. Existing publication deadline checks remain authoritative after compression.

Registration receipts are immutable scalar values. Minimum versions are ordered only within the same server and registry epoch; exact registration additionally requires RegistrationId. The server binds one immutable registration and validates before activation or business decoding. Runtime retirement remains the final lease gate. Local retirement signals contain no plugin Type, Assembly, proxy, or module references; pending unadmitted calls exit while already admitted calls drain.

## Consequences and validation

Proxy Get/CreateProxy is local-only; readiness is advisory and calls independently admit. A pool never substitutes an incompatible endpoint merely because another compatible endpoint was observed. Streams remain pinned to the admitted session. Retry eligibility is unchanged and admission does not replay a non-idempotent call.

Validation requires all five invocation shapes, mixed endpoints, same-hash replacements, independent timeline identities, delayed manifests, waiter bounds/cancellation/deadlines/stop, collectible-module retirement, real transport, NativeAOT, and exact baseline/head hot-path performance. Standalone #775 preserves generated API4; integration with #762 must retain its separately reviewed API5 identity and this wire version.
