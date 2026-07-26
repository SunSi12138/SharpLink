# SharpLink 0.8.20 migration guide

Chinese: [`../migration-0.8.20.md`](../migration-0.8.20.md)

0.8.20 does not change Protocol v2 framing or the generated Manifest. `SharpLinkProtocolOptions.HandshakeTimeout`, `SharedMemoryTransportOptions.HandshakeTimeout`, and Client/Server TLS handshake timeouts must now be positive and no greater than 2,147,483,647 ms (about 24.8 days); larger values throw `ArgumentOutOfRangeException` during configuration. Model an indefinite wait through caller-owned cancellable lifecycle control rather than a multi-year handshake timeout.

Far-future RPC absolute deadlines remain supported. Disconnected readiness, full pending-table admission, and Server graceful drain are sliced within the portable timer range, preserving the existing competition among caller cancellation, owner completion, and the real deadline.

Generated DTO string fields now require valid UTF-8. Older versions silently replaced malformed bytes with U+FFFD; 0.8.20 throws `SharpLinkException` with `DataLoss`. A correctly encoded U+FFFD still round-trips, so valid text data needs no change.
