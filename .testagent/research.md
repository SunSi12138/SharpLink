# 0.8.36 regression-test research

## Candidate inventory

- `SharpLinkServer.TryAcquireCall` checks `Running`, acquires connection capacity, checks
  `Running` again, and only then increments `_globalActiveCalls`. Stop can transition to
  Draining and observe zero between the second check and the increment, while the method still
  returns `Acquired`.
- Retired connection cleanup awaits `ServerConnectionState.ServiceCleanupTask` in an untracked
  fire-and-forget task. Server Stop joins connection handlers and sessions, but can return before a
  connection-scoped service finishes asynchronous disposal.
- Performance-profile defaults infer whether `MaxSendQueueBytes` was configured by comparing its
  value to 8 MiB. An explicit 8 MiB value is therefore overwritten by LowLatency or Throughput.
- `SharpLinkCallOptions.EnableCompression` is a public switch whose only consumer throws
  `Unimplemented` for every `true` value, including sessions where compression was configured and
  negotiated. Compression is otherwise automatic, so the switch is a dead and misleading API.
- Handshake response encoding and decoding accept a compression profile when Compression is not
  negotiated, and accept negotiated Compression without a profile. The Client repairs this only
  in a later caller-specific check, leaving the public codec able to emit and return semantically
  invalid protocol values.

## Acceptance boundary

- No call may be admitted after Server leaves Running, even if Stop races the final global-count
  publication; rollback must preserve both global and connection counters.
- A normally completed Server Stop must join connection-service cleanup that started as part of
  closing its owned connections, while the existing bounded forced-stop policy remains intact.
- Profile defaults apply only when the queue was never explicitly assigned; explicitly assigning
  the nominal 8 MiB default must be preserved in frozen options.
- Remove the unusable per-call compression switch and document automatic negotiated compression;
  do not invent partial force-compression semantics for only the initial request frame.
- Both outbound and inbound handshake response codec boundaries reject profile/capability
  incoherence before a session can consume it.

## Planned evidence

- Use a bounded admission-race probe plus exact counter assertions; retain only a deterministic or
  repeatedly witnessed pre-fix failure.
- Use a blocked `IAsyncDisposable` connection service and assert that Stop remains incomplete until
  disposal is released.
- Freeze Throughput options after explicitly assigning 8 MiB and assert the exact value.
- Assert the SDK surface no longer exposes the dead switch; preserve a pre-fix runtime probe showing
  `true` always fails despite configured compression.
- Exercise all four handshake response coherence combinations at both writer and reader boundaries.

## Assertion and pseudo-mutation review

- Removing the final post-increment state check, or forgetting either counter rollback, must fail
  admission/counter assertions.
- Replacing tracked retired cleanup with fire-and-forget must let Stop complete while disposal is
  blocked.
- Dropping the explicit-configuration bit must restore the 8 -> 32 MiB overwrite.
- Reintroducing the SDK property must fail the public-surface assertion; compression configuration
  and negotiated automatic compression tests remain unchanged.
- Dropping either writer or reader coherence validation, or checking only one direction, must fail a
  distinct assertion.
