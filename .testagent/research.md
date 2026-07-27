# 0.8.32 regression-test research

## Bounded target inventory

- Unix socket capture gap: a filesystem UDS is bound before its type/device/inode identity is captured. If the path is replaced in that gap and capture fails, cleanup receives a null identity and disposes the socket without preserving the replacement, reopening the caller-data deletion fixed for the ordinary dispose path in 0.8.31.
- Compression negotiation identity: runtime options retain provider objects and reread `WireProfile` during client advertisement, server selection, lookup, session diagnostics, and errors. A mutable provider can therefore change the protocol identity after Build even though builder configuration is documented as frozen.
- Authentication provider boundary: a rejected primary `SharpLinkAuthenticationResult` with an undefined nonzero `SharpLinkErrorCode` passes server normalization, then faults the binary error writer instead of returning a stable authentication rejection.
- Extreme positive call timeout: builder/call documentation accepts any positive `TimeSpan`, but `DateTimeOffset.UtcNow.Add(timeout)` throws for `TimeSpan.MaxValue`. A configured default therefore builds successfully and fails every RPC before sending; the monotonic deadline path already supports saturation.
- Admission immediate path: every admitted request creates a fixed eight-slot array, a retained-lease array, and an acquired-lease array even when it never queues. These are short-lived Gen0 allocations on an explicitly throughput-sensitive server path; the common single-concurrency rule needs neither oversized slots nor retained/acquired arrays.

## Engineering boundary

- Preserve any path entry when a bound Unix socket has no captured identity; a possible stale socket is safer than deleting an entry whose ownership cannot be proven.
- Capture each validated profile/provider pair once in the runtime snapshot. Provider execution remains delegated to the original thread-safe instance; only mutable wire identity is frozen.
- Normalize undefined authentication codes at the server trust boundary even when a provider bypasses the `Reject` factory through the public primary constructor.
- Saturate positive timeouts at `DateTimeOffset.MaxValue`; preserve rejection of zero/negative values and earlier explicit deadlines.
- Size admission slots exactly and transfer a single acquired lease directly for one non-retained limiter. Preserve the general multi-rule and queued ownership path. A measured shared-pool implementation is unacceptable if rent/return overhead regresses latency.

## Acceptance checklist

- Cleanup with a bound path but no captured identity preserves a replacement file.
- A provider profile mutation after Build cannot alter lookup or negotiation identity.
- Undefined provider rejection codes reach the peer as `AuthenticationRejected` rather than faulting the handshake encoder.
- `TimeSpan.MaxValue` default request timeout sends a cancellable request with a valid far-future deadline.
- Warm immediate admission stays below a measured per-call allocation ceiling after eliminating its three framework-owned arrays.
- Existing uncompressed, built-in compression, authentication, timeout, and UDS paths remain stable.

## Deferred/rejected signals

The shared-memory reader contains one duplicate `SetNext` call; it is harmless cleanup. Multi-cluster deferred unregister polling after Faulted state is not promoted because the owning client still requires explicit disposal and already retains the registration itself. A custom compression-provider overrun was also rejected: the runtime supplies an exact-capacity leased `PooledByteBufferWriter`, and the pre-fix proof showed it already throws before exposing one byte beyond the negotiated/declared bound. The first admission implementation pooled bounded arrays and reduced allocation to 232 B/call, but its 93.996 ns mean regressed the 58.477 ns baseline by about 60.7%; it was removed. Exact slots plus the direct single-lease path measured 49.262 ns / 288 B.
