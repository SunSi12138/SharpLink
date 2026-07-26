# 0.8.14 regression-test research

## Target inventory and verified candidates

- `NamedPipeName.Normalize`: Unix socket limits are byte limits, but the implementation budgeted UTF-16 characters for both the temp path and logical name. A 30-character CJK name produced a 150-byte native path against the 103-byte limit.
- `NamedPipeServerTransportListener`: constructor validation rejected only zero; `-2` and `255` survived configuration and failed later when an accept created the native pipe.
- `PendingRequestTable.PendingCall.CancelProducer`: application callbacks on the producer token could throw out of terminal completion after the slot was removed, leaving the operation and pooled call stranded.
- `SocketClientTransportFactory`: remote TCP/DNS port zero was accepted although it is only useful for server bind and every other Client endpoint API specifies 1 through 65535.
- `StreamFlowController`: a FIFO head blocked only by its own stream credit prevented later streams from using available connection and per-stream credit, causing cross-stream head-of-line blocking.
- A proposed multi-cluster cancellation-callback candidate was withdrawn after token-ownership review: child clients intentionally detach caller wait cancellation from the shared connect attempt, and transports must not retain an operation token after return.

## Acceptance checklist

- Normalized Unix named-pipe paths stay within 103 UTF-8 bytes without splitting surrogate pairs; Windows names remain unchanged.
- Named-pipe server instance limits accept only `-1` or 1 through 254 at construction.
- Throwing producer cancellation callbacks are reported but cannot interrupt pending completion or replace its terminal failure.
- Client TCP/DNS remote endpoints reject port zero at construction; server ephemeral bind remains unchanged.
- A waiter lacking only stream credit can be bypassed by another eligible stream, while connection-credit contention retains FIFO order.

## Audit guardrails

This pass continues transport validation, client-stream lifecycle, and multiplexed flow-control review. Delayed failures were promoted only where the public configuration contract is explicit or where they can terminate a server run loop. The withdrawn multi-cluster hypothesis is retained here to document why it is not counted.
