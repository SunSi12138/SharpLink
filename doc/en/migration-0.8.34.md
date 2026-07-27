# SharpLink 0.8.34 migration guide

Chinese: [`../migration-0.8.34.md`](../migration-0.8.34.md)

0.8.34 changes no Protocol v2 framing, route hash, or valid payload. Inherited declarations with the same CLR signature must now agree on return type, Oneway shape, Timeout/Idempotent/NonCancellable policy, serialized parameter names, and full nullability schema. CancellationToken/CallOptions names are control metadata and remain interchangeable. A conflict reports one `SHARPLINK057` and suppresses Proxy/Stub output. An explicit derived `new` redeclaration may select canonical metadata; incompatible return types still require renaming or contract separation.

`LogEvents.Client.ConnectionAttemptFailed = 6101` is an additive public constant. Handled fixed/static/dynamic expansion and reconnect failures move from Error `6002` to Warning `6101`; genuinely unhandled background failures remain Error `6002`. Update event-ID alerts accordingly. Shared-memory ownership, terminal `AdvanceTo`, and Chaos-oracle fixes require no application migration.
