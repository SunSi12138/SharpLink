# 0.8.41 regression-test research

## Candidate inventory

- `RpcRequestOperation<T>` accepts a null decoded scalar response regardless of generated
  `ResponseNullable`; a custom or mismatched peer can therefore return null for required unary and
  client-streaming results outside the interceptor path.
- The Client response-stream `PooledAsyncStreamDispatcher<T>` similarly enqueues null for required
  ServerStreaming/DuplexStreaming items.
- The Server uses the same dispatcher for ClientStreaming/DuplexStreaming request items but generated
  Stubs do not pass `PayloadNullable`, so required application stream inputs also accept null.
- Response nullability is present in the contract Manifest but absent from the runtime method
  fingerprint. Required and nullable inherited response contracts therefore appear identical during
  generated conflict checks and live route compatibility.
- `SharpLinkErrorCode.Unknown` is reserved for unset local state and 0.8.40 prevents constructing a
  service error with it, but Protocol v2 still writes and accepts it as a remote Error code.

## Acceptance boundary

- Scalar response operations reject decoded null as `DataLoss` unless the generated descriptor marks
  the response nullable; propagate the flag through every unary/retry/client-streaming rent path.
- Stream dispatchers reject decoded null as `DataLoss` by default. Client response streams pass
  `ResponseNullable`; generated Server request streams pass each parameter's `PayloadNullable`.
- Nullable response contracts differ in method/service/contract fingerprints without changing method
  IDs, wire type names, required-response fingerprints, or payload layout.
- Protocol Error writer, validator, and reader reject reserved `Unknown`; concrete defined codes keep
  their existing wire values.

## Planned evidence and pseudo-mutations

- Dispatch a null-returning codec through `PendingRequestTable`; require required failure and a later
  nullable control.
- Exercise both stream-dispatcher Rent paths: the Client codec overload and Server codec-provider
  overload must independently reject required null; later nullable controls must remain valid.
- Build inherited contracts that differ only in response nullability and require `SHARPLINK057` while
  preserving the existing request-nullability behavior.
- Prove both `WriteError(Unknown)` and a raw Unknown Error payload are rejected, with a concrete-code
  round-trip as the control.
- A scalar-only fix, Client-stream-only wiring, Server-stream-only wiring, display-type hashing, or
  writer-only Unknown guard must leave at least one witness failing.

## Preserved pre-fix evidence

- Exact baseline: local 0.8.40 commit `dd431f5b18fc0e98b24a5f6edddd45a320d8fe23`.
- Generator suite: 119 established tests passed and only the new separate-compilation response
  fingerprint witness failed. The inherited-nullability diagnostic control already passed through
  Roslyn symbol comparison and was not counted as evidence.
- Unit suite: all 486 established tests passed and exactly four new witnesses failed: reserved
  Unknown Error, required scalar null, Client response-stream null, and Server request-stream null.

## Assertion and pseudo-mutation review

- Scalar required and nullable operations share the same null-returning codec; omitting any
  PendingRequestTable/RpcRequestOperation flag propagation fails one side of the paired assertion.
- Client codec and Server codec-provider stream Rent paths have independent required failures and
  nullable controls. Existing optional ServerStreaming plus a real optional ClientStreaming control
  verify both generated wiring directions.
- Fingerprints are compared across separate compilations of the same fully qualified contract, so
  nullable C# display output cannot satisfy the witness. Only the runtime method schema changes;
  required response fingerprints and method IDs retain their prior inputs.
- Unknown is exercised at writer and raw-reader boundaries. The existing ResourceExhausted
  round-trip and undefined-code tests preserve concrete code compatibility and partial-write safety.
