# 0.8.25 regression-test research

## Target inventory and evidence candidates

- Sanitizing fully-qualified contract names by replacing every punctuation character with `_` can produce duplicate Roslyn hint names; nested contracts with the same simple name also emit colliding top-level Proxy/Stub/helper types.
- C# keyword method and parameter names lose their source escape marker in Roslyn symbols and are emitted as invalid syntax or the wrong `default` expression.
- `ref`, `out`, `in`, and by-ref return signatures pass RPC method analysis even though the wire model and generated implementation cannot represent them.
- Static abstract interface RPC methods pass ordinary-method analysis but generated instance proxies cannot implement them.
- Abstract properties, indexers, and events on an RPC contract are silently ignored, leaving generated proxies incomplete.

## Acceptance checklist

- Hint names are collision-resistant and public nested contracts receive deterministic unique generated peer names.
- Every emitted source identifier is escaped without changing the raw contract/member identity used for hashes and Manifests.
- Unsupported by-ref, static abstract, property, indexer, and event surfaces produce stable compile-time errors and suppress incomplete artifacts.
- Existing top-level generated type names and valid instance method output remain source-compatible.
- Generator latency and allocation show no material regression.

## Audit guardrails

The native unmanaged fallback remains a real ABI/padding risk, but changing it would alter existing native wire behavior and requires a separately designed migration. It is retained as an open audit candidate rather than being forced into this batch.
