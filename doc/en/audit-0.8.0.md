# SharpLink 0.8.0 Deep Audit

Chinese: [`../audit-0.8.0.md`](../audit-0.8.0.md)

This batch follows the rule that five evidenced P2-or-higher improvements advance one patch release. The baseline is tag `v0.7.11`, commit `0151db10c89c8067859daef06ef04e2905cd0e89`. Every candidate was reproduced before repair. A suspected control-only overload collision was removed from scope after the existing `SHARPLINK027` diagnostic proved it was already handled.

The five confirmed fixes are: exact native Codec payload consumption; canonical Boolean and nullable markers; complete cross-stream connection-credit flushing; inherited RPC contract method discovery with redeclaration de-duplication; and routing user-defined/nullable unmanaged request values through their selected length-delimited Codec instead of native blitting.

Pre-fix evidence consisted of six failing Unit assertions and two failing Generator assertions. Final focused coverage includes contiguous and segmented malformed payloads, all built-in nullable marker shapes, exact WindowUpdate frames from a real session, inherited-only methods, redeclarations, and selected Adapter calls. See [`../performance-0.8.0.md`](../performance-0.8.0.md) and [`../migration-0.8.0.md`](../migration-0.8.0.md).
