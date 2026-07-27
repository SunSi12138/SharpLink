# SharpLink 0.8.24 migration guide

Chinese: [`../migration-0.8.24.md`](../migration-0.8.24.md)

0.8.24 does not change Protocol v2 or payload layouts. Valid 0.8.23 RPC contracts retain the same request, response, and stream wire shapes.

An explicit `[Timeout(seconds)]` must produce a positive finite `TimeSpan`. Zero, negative, NaN, Infinity, values that round down to zero, and values outside the `TimeSpan` range now report `SHARPLINK050`. `[RpcUnionCase]` tags must be positive; every case must be a closed concrete class or struct assignable to the annotated union, and one case type may bind to only one tag. Violations report `SHARPLINK051`.

`[assembly: SharpLinkRpcContracts()]` now explicitly means “scan no referenced contract assemblies.” Remove the empty attribute if existing code intended automatic discovery. Existing filters with marker types are unchanged.

Generated JSON and assembly Manifests now obtain `generatorVersion` from the actual Generator package instead of the stale fixed `0.8.3` value. Because `schemaFingerprint` protects the complete JSON, rebuilding produces a new fingerprint. An older baseline is still integrity-checked using its own generator version before structural compatibility comparison; do not edit it manually.
