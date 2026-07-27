# SharpLink 0.8.2 Deep Audit

Chinese: [`../audit-0.8.2.md`](../audit-0.8.2.md)

This batch audited client connection lifetime, endpoint discovery, and Protocol v2 text/length boundaries from 0.8.1 commit `5d30863`. Five P2 probes failed independently before the fixes and all 344 Unit tests passed afterward.

The fixes isolate caller cancellation from fixed-client shared initialization, preserve structured handshake-timeout causes in both endpoint-cluster modes, restrict DNS last-good fallback to transient `SocketException`, reject overlong VarUInt32 lengths, and validate binary error messages as strict UTF-8 in both frame validation and decoding. Valid wire layouts remain unchanged; see [`../performance-0.8.2.md`](../performance-0.8.2.md) and [`../migration-0.8.2.md`](../migration-0.8.2.md).
