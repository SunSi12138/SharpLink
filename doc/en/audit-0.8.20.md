# SharpLink 0.8.20 deep audit

Chinese: [`../audit-0.8.20.md`](../audit-0.8.20.md)

Against 0.8.19 commit `2d7cd95`, this batch confirmed five P2-or-higher defects: RPC, TLS, and shared-memory handshake configuration accepted values beyond the portable native timer range; a disconnected `WaitForReady` call with a far-future deadline failed immediately in `Task.WaitAsync`; a full pending table did the same in `SemaphoreSlim.WaitAsync`; Server graceful Stop handed a saturated monotonic deadline to a native wait and forced an immediate stop; and generated DTO strings silently normalized malformed wire bytes to U+FFFD through replacement UTF-8 decoding.

The complete pre-fix Unit probe contained 441 tests: all 436 existing tests passed and exactly the five new tests failed. The two Client deadline probes captured immediate `ArgumentOutOfRangeException`, the Server wait ended or faulted before its owner, all three handshake families accepted invalid configuration, and neither contiguous nor segmented malformed UTF-8 produced `DataLoss`. Each recommendation is therefore tied to reproducible externally visible behavior rather than style-only inspection.

The final implementation centralizes portable timer slicing. Handshake configuration rejects over-range values before connection or transport ownership is acquired, while far-future readiness, pending admission, and Server drain remain cancellable and completable. Generated strings keep the normal `Encoding.UTF8` decoder and strictly revalidate the original bytes only when the decoded result contains U+FFFD, rejecting malformed input while preserving valid encoded U+FFFD.

After the fixes, the non-incremental Release build completed with 0 warnings and 0 errors; Generator 83/83, Unit 441/441, Integration 230/230, the seven-package pack, and fresh-cache package smoke all passed. See [`migration-0.8.20.md`](migration-0.8.20.md) and [`performance-0.8.20.md`](performance-0.8.20.md).
