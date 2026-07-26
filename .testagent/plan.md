# 0.8.2 regression-test plan

1. [x] Prove caller cancellation cannot own the shared fixed-client connect lifetime.
2. [x] Exercise handshake timeout classification through static and dynamic endpoint clusters.
3. [x] Distinguish transient DNS lookup failures from unexpected resolver implementation failures in Resolve and Watch.
4. [x] Reject overlong VarUInt32 lengths through direct metadata and full-frame parsing.
5. [x] Reject invalid UTF-8 through direct error decoding and frame-shape validation.
6. [x] Complete final versioned Release, Generator, Unit, Integration, performance, and diff gates.
