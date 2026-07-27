# SharpLink 0.8.25 migration guide

Chinese: [`../migration-0.8.25.md`](../migration-0.8.25.md)

0.8.25 does not change Protocol v2, route hashes, payload layouts, the Manifest schema, or generated Proxy/Stub type names for top-level contracts. Roslyn hint names are build-internal identities; appending the stable contract ID does not affect caller source.

Public nested contracts now receive unique generated peer names that include containing-type identity. If code directly references an older nested `IInner_Proxy` or `IInner_Stub` name, prefer the Client/Server contract APIs or update it to the name shown in generated source. Normal `Get<TContract>()` and Manifest registration need no change. The contract and every containing type must be public (`SHARPLINK055`); a generic containing type reports `SHARPLINK005`.

Legal C# keyword method and parameter names now generate correctly. `ref`, `out`, `in`, and by-ref returns report `SHARPLINK052`; static methods report `SHARPLINK053`; abstract properties, indexers, and events report `SHARPLINK054`. These surfaces previously had no compilable or complete generated proxy, so there is no usable wire contract to preserve. Default interface members with implementations may remain as local helpers and do not become RPC routes.
