Final producer correction 8af020df / source 8915e61e is measured against c242, not against a moving prerequisite.

- 40/40 bounded launches pass across runtimes 10.0.2/10.0.13. Twenty-four launches use enrolled TLS/auth owners, pool two and a successful compatible update; sixteen separately exercise retry-enabled ordinary unary success.
- Nine ordinary object layouts and ten selection IL/local signatures match. Actual generated client-stream state/box stays 272/344 B; the final state has 17 fields.
- The obsolete registration interface call and callback are removed. The existing decrement moves to the correct nonproducer/whole-producer boundary, with no new catalog/update query, counter or per-item work.
- Not cost-neutral: client-stream optimized entry adds one 64-byte vector zero store; Tier0/Instrumented reserves 64 B more and writes 56 B more zeros. Duplex Tier0/Instrumented adds one 16-byte zero store. Accelerated-PGO unary/retry frames also grow 376→392 B / 456→472 B, with unchanged entry zeroing and changed placement of the existing release work.
- Whole-process allocation samples include both favorable and adverse results. They are not isolated per-invoker allocation or elapsed-time evidence. All raw and profile-variant samples remain available; no throughput-equivalence claim or invented budget.
