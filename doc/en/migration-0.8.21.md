# SharpLink 0.8.21 migration guide

Chinese: [`../migration-0.8.21.md`](../migration-0.8.21.md)

0.8.21 does not change Protocol v2 wire formats or generated Manifests. Generated DTO strings and `SharpLinkMetadata` keys and values must contain valid Unicode. Local values with isolated high or low surrogates now throw `EncoderFallbackException` before encoding instead of sending U+FFFD. Valid surrogate pairs, including emoji, are unchanged.

Malformed shared-memory mapping paths now fail the handshake with `FailedPrecondition` rather than reaching filesystem validation as replacement text. Null generated collections with trailing bytes now report `DataLoss`. A dynamic per-call DI scope factory failure preserves its original exception without blocking module unload.
