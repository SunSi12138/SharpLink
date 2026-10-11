# Terminal stream credit correctness

## Reproduced defect

A queued OneWay call can fail before consuming its client stream. Its exception
mapper may keep the completed call state in the request map while the connection
receive loop continues to process stream frames. The previous terminal-call gate
skipped those frames before receive accounting. The sender had spent credit, but
the receiver neither debited nor refunded it, so the sender could stall forever.

The deterministic regression holds the mapper, publishes one 64-byte stream
window, and fences the same connection with a normal-priority HealthCheck. Credit
must return while the mapper is still held. The existing two-second drain test
keeps its original timeout and assertions.

## Transport-only terminal handling

- The business/user-code terminal gate remains closed.
- Only an existing, already-discarding stream route can account terminal data.
  This path neither creates unknown routes nor changes a live typed consumer.
- Each frame uses one receive debit and one discard refund. Raw compressed frames
  validate their envelope and declared original size without running the decoder.
  A terminal transition after decode accounts decoded bytes once and returns the
  decoded owner in a finally block.
- Stream completion removes only the exact captured route entry. Replacements
  under the same request/stream identifiers cannot be completed accidentally.
- A stale pooled call-state lease retries current ownership with a fixed bound;
  losing an old generation is not interpreted as the new call being terminal.
- Every OneWay success/failure path establishes discard before publishing call
  completion or entering an exception mapper. Existing final cleanup remains.

## Scope and validation

This change does not alter public APIs, generated Stub contracts, generated ABI
identity/version, wire frames, method-fact resolution or outbound method snapshots.
It is independent of the MethodShape performance proposal in PR #754.

Tests cover exact raw/compressed credit totals, buffered-byte release, absent and
live routes, invalid lengths, terminal transitions during decode and decode
failure, duplicate/late data, route replacement and stale pooled generations.
The focused before/after test and full repository validation must run against the
standalone branch before acceptance; prior PR #754 results are reference evidence,
not a substitute for validating this extraction.
