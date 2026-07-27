# 0.8.42 regression-test research

## Candidate inventory

- Throughput-profile timed batching repeatedly cancels the single-reader Channel waiter at its 1 ms
  deadline. Under streaming load, producer completion can race that cancellation and resume
  `WaitForMoreUntilDeadlineAsync` twice, terminating the process with `InvalidOperationException`.
- Non-nullable `Memory<T>` and `ReadOnlyMemory<T>` built-in Codecs accept the array/list-only `-1`
  null marker and silently coerce it to empty memory.
- Fixed-width nullable primitive Codecs validate the 0/1 marker but accept arbitrary ignored value
  bytes when the marker is null, creating multiple non-canonical encodings for the same value.
- Public cancel/health and handshake writers reuse peer validators, so invalid local values throw
  `SharpLinkException(ProtocolViolation)` instead of argument exceptions.
- Generated runtime Codec `SchemaId` omits DTO member nullability even though generated decoding and
  contract manifests distinguish required and nullable members.

## Acceptance boundary

- Timed batching keeps at most one outstanding Channel read wait. A deadline races that wait against
  a separate delay without cancelling the underlying read; a timed-out wait is retained and consumed
  by the next pump iteration.
- Memory shapes reject `-1` as `DataLoss`; arrays, lists, and default `ImmutableArray<T>` retain their
  existing null/default representations.
- Every fixed nullable null payload requires zero value bytes on contiguous and segmented input;
  canonical null and all present values remain unchanged.
- Local control and handshake writer validation throws argument exceptions before advancing the
  writer; inbound readers continue to classify the same bits as `ProtocolViolation`.
- DTO member nullability contributes to runtime Codec schema identity without changing field IDs or
  valid payload bytes.

## Preserved pre-fix evidence

- Exact baseline is local 0.8.41 commit `d0e0df4e3e81b9ead5c416c9a33ebd459c55f5a1`.
- The independent exact-baseline Throughput load exited 134 twice with the same stack rooted at
  `RpcSession.SendPump.WaitForMoreUntilDeadlineAsync`; focused samples crashed in 3/5 s2c and 5/5 c2s
  0.8.41 processes. Evidence is preserved under
  `../feature-perf-0.7.11-vs-0.8.41-20260727-a/artifacts/raw/`.
- Generator: all 120 established tests passed and only the new separate-compilation DTO SchemaId
  witness failed.
- Unit: all 490 established assertions outside the modified witnesses passed; exactly three tests
  failed, independently proving Memory null coercion, non-canonical nullable bodies, and local writer
  error misclassification.

## Pseudo-mutation review

- Sixteen post-fix repetitions of the identical `operation=all`, Throughput-profile TCP reproducer
  completed all 64 unary/c2s/s2c/duplex stages with zero failures. Raw evidence is preserved under
  `artifacts/0.8.42-sendpump-repro/` and `artifacts/performance/0.8.42-throughput-final/`.
- Array/list/default-immutable controls prevent a blanket rejection of `-1` collection markers.
- Three distinct nullable sizes and segmented input prevent a marker-only or one-codec fix.
- Cancel/health and handshake limits use independent witnesses, untouched writers, and verify both
  exception family and pre-write validation.
- DTO schemas are compared across separate compilations of the same full type identity, isolating
  member nullability from names, IDs, and traversal order.
