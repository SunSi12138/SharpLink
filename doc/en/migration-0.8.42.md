# SharpLink 0.8.42 migration guide

Version 0.8.42 does not change valid Protocol v2 framing, method/field IDs, or valid payload bytes. It rejects previously tolerated non-canonical input and corrects local writer error classification.

Non-nullable `Memory<T>` and `ReadOnlyMemory<T>` now reject the `-1` collection marker as `SharpLinkException(DataLoss)` instead of coercing it to empty. Nullable arrays/lists and default `ImmutableArray<T>` retain their representations. A fixed-width nullable primitive null body must contain only zero value bytes; SharpLink serializers already emit this canonical form, while custom non-canonical producers now receive `DataLoss`.

Invalid local values passed to cancel/health or handshake writers now throw an `ArgumentException` subtype before advancing the writer. Readers continue to classify the same invalid peer bytes as `SharpLinkException(ProtocolViolation)`.

Nullable reference members now participate in generated runtime Codec `SchemaId`. Separately built DTO contracts that differ only by member nullability are correctly incompatible, while established non-nullable schema identities, field IDs, and payload layouts remain unchanged. Regenerate nullable DTO artifacts from the same contract version on both sides.

The Throughput batching fix has no public API or wire-format effect; it removes a process-level race under concurrent streaming load.
