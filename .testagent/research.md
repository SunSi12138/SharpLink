# 0.8.39 regression-test research

## Candidate inventory

- Server terminal invocation records only success. When the terminal throws, interceptor frames
  unwind before the outer pipeline records failure, so an interceptor catching `next` observes a
  still-Pending context with no error code or exception. The equivalent Client terminal records
  failure before unwinding.
- A Server interceptor can return successfully without invoking `next` for a response-bearing
  call. The pipeline marks the context Succeeded and sends an empty success response that fails
  later in the Client Codec; the Server API provides no way for that interceptor to create a
  replacement response.
- Client interceptor result type validation occurs after the tracked pipeline has marked the
  context Succeeded. A wrong short-circuit type therefore throws `InvalidCastException` to the
  caller while the retained context claims success.
- `ClientConnection.SendClientStreamAsync` is the only framework `await foreach` over application
  stream input that does not use `ConfigureAwait(false)`. An incomplete `MoveNextAsync` can post
  every producer continuation back to a caller synchronization context.
- Generated fixed/request decoders and `RpcEmptyRequestCodec` use `InvalidDataException` for
  malformed wire input, while generated DTO and built-in Codec boundaries use structured
  `SharpLinkErrorCode.DataLoss`. On the Server, the default exception mapper turns the former into
  `Internal`, misclassifying peer-controlled malformed request data as an application failure.

## Acceptance boundary

- Record Server terminal failure/cancellation status, code, exception, and elapsed time before
  unwinding through interceptor code; retain the outer catch as the pipeline-level fallback.
- For response-bearing Server calls, returning from an interceptor without invoking its
  continuation fails locally as `Internal`; OneWay interception retains its existing no-response
  behavior.
- Validate every Client interceptor result inside the tracked pipeline before success is
  published. Correct unary, stream, one-way, and short-circuit results remain unchanged.
- Configure only the framework-side client-stream enumeration await; user iterator internals keep
  their own context semantics.
- Generated/empty request wire validation throws structured DataLoss without changing valid bytes
  or mapping arbitrary application `InvalidDataException` to a public peer error.

## Planned evidence

- Add a Server interceptor that catches a failing service continuation and snapshots context state
  before rethrowing.
- Add a no-next Server interceptor for a value-returning call and distinguish the current Client
  decode failure from the intended Server-side structured failure.
- Add a wrong-type Client short circuit and retain its context after caller failure.
- Use a controlled incomplete `IAsyncEnumerator.MoveNextAsync` plus a counting synchronization
  context to prove the framework consumer posts back today.
- Execute generated request Codec and Stub malformed shapes for non-canonical Boolean, truncation,
  invalid length/null, trailing bytes, and empty requests, asserting DataLoss at each boundary.

## Assertion and pseudo-mutation review

- Updating only the outer Server pipeline must leave the interceptor's pre-unwind snapshot failing;
  updating only the terminal must leave no-next detection failing.
- A simple non-null result check must not satisfy wrong scalar, wrong stream, or non-null OneWay
  results; positive short-circuit controls prevent rejecting valid results.
- Moving user production to `Task.Run` would hide the synchronization-context witness but add a
  task hop; the exact `ConfigureAwait(false)` assertion preserves the allocation-free synchronous
  fast path.
- Mapping all `InvalidDataException` in the public exception mapper must fail the application-code
  control; the generated trust boundary itself owns DataLoss classification.
