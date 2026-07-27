# 0.8.21 regression-test research

## Target inventory and evidence candidates

- Shared-memory server-response mapping paths use replacement UTF-8 decoding before security-sensitive path validation, so malformed wire bytes are normalized rather than rejected at the handshake boundary.
- Generated collection codecs return immediately for a null marker without checking that the root payload is fully consumed, accepting hidden trailing bytes that every non-null collection rejects.
- Generated DTO string serialization uses replacement UTF-8 encoding, silently changing a .NET string containing an isolated surrogate into U+FFFD on the wire.
- `SharpLinkMetadata` accepts isolated surrogates in keys and values; Protocol v2 later replacement-encodes them, so the peer observes different routing or contextual metadata.
- Dynamic per-call service acquisition creates its DI scope outside the cleanup region. If `IServiceScopeFactory.CreateScope()` throws, the already-acquired module call lease is never released and module draining cannot complete.

## Acceptance checklist

- Malformed shared-memory path bytes fail as a handshake validation error before filesystem path normalization.
- Null generated collections reject trailing bytes with `DataLoss`.
- Generated string writers reject isolated surrogates before writing a length or payload.
- Metadata snapshots reject invalid Unicode during construction rather than changing it during a later call.
- Per-call scope creation failure releases its dynamic module lease, while preserving the original scope-factory exception.

## Audit guardrails

The compression-output-limit candidate was discarded after verifying that pooled writer leases already hard-bound both compression and decompression output to the negotiated size. Error-message replacement, native-endian legacy wire cleanup, and unused helpers remain lower-priority follow-up unless they gain independent compatibility or performance evidence.

## Regression and performance evidence

Against clean 0.8.20 commit `726992c`, the complete pre-fix Unit run contained 445 tests: all 441 existing tests passed and exactly four new probes failed. The metadata and generated writer accepted isolated surrogates, the per-call scope failure left one module call active, and malformed shared-memory path bytes were replacement-decoded before surfacing the wrong `PermissionDenied` classification. The complete Integration run contained 231 tests: all 230 existing tests passed and the one new generated null-collection trailing-byte probe failed.

After the focused fixes, Generator 83/83, Unit 445/445, and Integration 231/231 pass. Assertion and pseudo-mutation review confirms each probe independently detects replacement path decoding, the null early return, either replacement encoder, or scope creation outside cleanup. Metadata construction retained 136 B/op and baseline latency; strict metadata sizing adds about 2 ns and strict generated string output about 4 ns with zero allocation. A separate scan design was rejected because it roughly doubled metadata construction and was slower for short strings. Non-incremental Release build, seven-package pack, and fresh-cache package smoke also pass.
