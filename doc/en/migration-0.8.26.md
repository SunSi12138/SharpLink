# SharpLink 0.8.26 migration guide

Chinese: [`../migration-0.8.26.md`](../migration-0.8.26.md)

0.8.26 does not change Protocol v2, route hashes, payload layouts, the Manifest schema, or a valid public RPC surface.

An `[Oneway]` method must return non-generic `Task` or `ValueTask`. `Task<T>`, `ValueTask<T>`, and `IAsyncEnumerable<T>` now report `SHARPLINK056`; those shapes require a response or stream and cannot preserve Oneway semantics. Remove `[Oneway]` or change the return to a non-generic asynchronous completion type.

Implemented private/protected default interface helpers no longer enter the Manifest or generated Proxy/Stub. A non-public abstract method reports `SHARPLINK054`; make it public if it is intended to be an RPC. Generated-local avoidance and dictionary null-key `DataLoss` require no caller changes.

DTO members differing only by case now generate safely. A constructor parameter first matches a member by exact ordinal name. Without an exact match, the case-insensitive match must be unique or the DTO reports `SHARPLINK012`. Resolve that diagnostic by matching the intended member name exactly or by providing a writable-member/parameterless-constructor plan.
