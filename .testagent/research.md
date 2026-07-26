# 0.8.17 regression-test research

## Target inventory and evidence candidates

- `SharpLinkMultiClusterClient.UnregisterAssemblyAsync`: concurrent callers create independent coordinator operations over the same shared child operation. If the child rejects, both restore routes and one replaces the real failure with a route-conflict exception.
- `TlsAuthenticationOptionsSnapshot`: Client chain policy is retained by reference, while Server chain policy is dropped entirely; Server RSA signature-padding settings are also omitted on supported platforms.
- Protocol v2 handshake capability parsing: a request may require capabilities it did not advertise as supported, and a response may negotiate unknown capability bits.
- `AdmissionPartitionPool`: the live pool retains the mutable `SharpLinkPartitionAdmissionOptions` passed through the builder, so leaked callback state can alter limits and new partition runtimes after Build.
- `RuntimeConcurrencyOptions` and `BufferWriterPoolOptions`: valid-looking values can request unbounded stripe objects, aggregate map capacity, or retained writer memory.

## Acceptance checklist

- Concurrent coordinator unregister callers await one operation and observe the same original result or failure.
- TLS snapshots deep-clone chain policy, preserve Server chain policy, and retain supported RSA-padding settings.
- Handshake payload codecs reject inconsistent required/supported sets and unknown negotiated response bits before session state changes.
- Admission partition runtime owns a deep validated snapshot unaffected by later caller mutation.
- State-store and writer-pool validation enforce documented aggregate memory limits before allocation.

## Audit guardrails

The batch focuses on real concurrency, security configuration, protocol integrity, and bounded memory. The legacy process-wide generated registries and anonymous-pipe cross-process handle-transfer redesign remain separate compatibility topics rather than being mixed into this patch.

## Regression and performance evidence

- The complete pre-fix Unit run had exactly five focused failures among 427 tests; the post-fix run passes 427/427.
- The unregister test controls the rejected child operation and proves both callers receive the same original exception while only one child unregister is issued.
- TLS assertions cover both preservation and independence, capability probes exercise outbound and inbound malformed sets, admission assertions mutate nested source limits after construction, and sizing probes cross each aggregate hard limit.
- Four counterbalanced nine-sample A/B pairs with tiered compilation disabled retained identical allocations on all hot controls. Median timing ranges overlap for buffer-pool rent/return, pending completion, flow credit, handshake round trips, runtime-context lifecycle, and server lifecycle.
- Deep TLS policy cloning intentionally changes the cold snapshot from 96 B to 184 B and roughly 13 ns to 83 ns. Deep admission configuration cloning intentionally changes controller creation from 1,152 B to 1,224 B with low-single-digit-percent timing noise. Both costs occur at configuration/lifecycle boundaries and prevent mutable security or resource policy from leaking into live runtime state.
