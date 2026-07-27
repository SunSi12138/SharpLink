# Migrating to SharpLink 0.8.1

Chinese: [`../migration-0.8.1.md`](../migration-0.8.1.md)

0.8.1 adds no public API, but non-nullable request parameters of type `decimal`, `DateOnly`, `DateTime`, `DateTimeOffset`, `TimeOnly`, `Rune`, `Index`, or `Range` now use length-delimited built-in Codec framing instead of raw inline layout. Regenerate baselines and rebuild/deploy both peers for affected contracts.

Boolean requests retain their valid one-byte layout; encoders canonicalize to `0/1` and decoders reject other markers. Ordinary integers, floating-point values, `Half`, `Guid`, `TimeSpan`, `Int128`, and `UInt128` remain inline. Cast-and-mutate access to authentication scopes, endpoint snapshots, or generated manifest collections was never supported and is now blocked. Resolver and `List<T>` wire shapes are unchanged.
