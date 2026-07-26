# SharpLink 0.8.4 Migration Guide

Chinese: [`../migration-0.8.4.md`](../migration-0.8.4.md)

0.8.4 changes no public API, Protocol v2 wire layout, or generated Manifest version, so Client and Server can be rolled independently.

Codec lookup that races dynamic publication now retries against the current generation, and lookup crossing Runtime Context disposal throws `ObjectDisposedException`. Custom fallback resolvers and native generated factories should remain thread-safe and side-effect tolerant because a rare publication race can repeat resolution. Pre-admission buffered replay no longer blocks internal registration but retains the same bounded capacity and receive order. If multi-cluster old-generation cleanup fails after the child published its replacement, the cleanup exception still reaches the caller while the new route remains usable. No configuration migration is required.
