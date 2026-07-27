# 0.8.24 regression-test research

## Target inventory and evidence candidates

- C# attribute construction does not execute `TimeoutAttribute(double)` at compile time, so zero, negative, non-finite, and `TimeSpan`-overflowing constants currently reach generated descriptors unchecked.
- `RpcUnionCaseAttribute` documents a positive tag, but the current manifest accepts zero and negative tags.
- Union case metadata accepts abstract, open, unrelated, and multiply-tagged case types, producing a manifest that cannot describe a sound polymorphic mapping.
- An explicit empty `[assembly: SharpLinkRpcContracts()]` marker is treated as if no marker existed and falls back to scanning every SharpLink reference.
- Both generated assembly manifests and JSON contract manifests still hard-code generator version `0.8.3`, despite later package versions.

## Acceptance checklist

- Invalid timeout constants produce one stable generator error and never emit uncompilable or type-initializer-failing descriptors.
- Union tags must be positive; case types must be closed, concrete, assignable to the annotated union, and assigned exactly one tag.
- An explicit empty contract-assembly marker selects no referenced assemblies.
- Generated version metadata is derived from the executing generator assembly version so release bumps cannot leave stale provenance.
- Valid generator inputs and incremental output remain deterministic.

## Audit guardrails

The union shape conditions form one recommendation because they enforce the single invariant promised by `RpcUnionCaseAttribute`: a one-to-one mapping from positive wire tags to concrete cases of the annotated union. Timeout validity, explicit reference filtering, and release provenance are separate failure domains.
