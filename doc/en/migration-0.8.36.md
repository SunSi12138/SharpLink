# SharpLink 0.8.36 migration guide

Chinese: [`../migration-0.8.36.md`](../migration-0.8.36.md)

0.8.36 does not change valid Protocol v2 framing, route hashes, or business payloads. It tightens the existing handshake response consistency rule and removes one source-level API member.

`SharpLinkCallOptions.EnableCompression` has been removed. It had no successful path: `true` always threw `Unimplemented`, while `false` could not disable negotiated automatic compression. Remove that initializer. Configure compatible providers on Client and Server through `UseRuntime`; omit a provider on a sending side to disable its compression, or tune the three payload/savings thresholds.

An explicit `FlowControl.MaxSendQueueBytes = 8 * 1024 * 1024` now overrides profile defaults. Normal Stop joins asynchronous connection-service disposal after calls drain; an uncooperative call beyond the graceful timeout retains bounded deferred cleanup. Custom handshake tooling must supply Compression capability and a non-null selected profile together, or omit both.
