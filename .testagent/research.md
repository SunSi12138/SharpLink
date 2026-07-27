# 0.8.28 regression-test research

## Bounded target inventory

- `SocketTransportOptions`: keep-alive durations are validated only as positive, but the native option path performs a checked conversion to signed integer seconds.
- Admission rate policies: token, fixed-window, and sliding-window durations reach BCL timer-backed rate limiters without SharpLink's portable timer-range validation.
- Named-pipe transport constructors accept undefined `PipeOptions` and `PipeTransmissionMode` values, deferring unusable configuration until connect/accept.
- Sliding-window admission accepts more segments than the configured window has ticks, producing a zero-duration segment boundary for the timer-backed limiter.
- `ProtocolV2PayloadCodec.WriteError` emits undefined in-range `SharpLinkErrorCode` values even though its reader rejects the same wire value.

The repository convention is TUnit in `test/SharpLink.UnitTests`, with focused tests colocated in the existing transport, resolver, retry, admission, and flow-control suites. The required static source/test pairing scan was run once. It was polluted by retained ignored performance baseline clones under `artifacts/`, so its counts are only a heuristic; the live target files are already paired with `TransportValidationTests`, `DynamicEndpointResolverTests`, `SharpLinkClientRetryTests`, `AdmissionControlTests`, and `StreamFlowControllerTests` respectively.

## Acceptance checklist

- Both keep-alive duration fields reject values that cannot be converted to native signed integer seconds during configuration.
- All three timer-backed admission rate-policy durations reject values beyond the portable timer range before a runtime limiter is created.
- Named-pipe client and server constructors reject undefined option bits and transmission modes synchronously; clients also reject server-only `FirstPipeInstance`.
- Sliding-window admission rejects configurations whose segment duration would be zero ticks.
- Binary error writers reject undefined error codes before emitting an unreadable payload.
- Existing normal-boundary behavior remains covered and hot-path performance does not materially regress.

## Audit guardrails

Direct string wire encoding, arbitrary unmanaged ABI/padding, and external-process anonymous-pipe handle transfer still require versioned/public API designs and are not opportunistically changed. The suspected pending-table Dispose/Rent race is not counted until a deterministic pre-fix witness exists.

## Rejected hypotheses

- DNS and retry jitter at `TimeSpan.MaxValue` saturate rather than wrap on the supported .NET 10 runtime; repeated boundary probes passed.
- Excess flow-control credit near `int.MaxValue` is already normalized to `ProtocolViolation`; the boundary probe passed.
