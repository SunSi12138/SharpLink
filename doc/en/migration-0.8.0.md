# Migrating to SharpLink 0.8.0

Chinese: [`../migration-0.8.0.md`](../migration-0.8.0.md)

0.8.0 is a pre-1.0 wire-correctness update. It adds no public API, but generated request layouts and derived-contract fingerprints can change.

Rebuild and deploy both peers together when a contract uses user-defined unmanaged request structs, nullable unmanaged request values, or RPC methods inherited from an ordinary base interface. These parameters now use length-delimited selected Codecs, and inherited methods now participate in proxy/stub generation, manifests, and fingerprints. Regenerate development contract baselines. Do not mix 0.7.11 and 0.8.0 peers for affected contracts.

Valid built-in payloads are unchanged. Native fixed, string, and collection Codecs now reject trailing bytes, and Boolean/nullable markers accept only canonical values. Stream flow-control framing is unchanged; the runtime now emits legal credit updates that 0.7.11 could strand.
