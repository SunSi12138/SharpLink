# SharpLink 0.8.44 migration guide

Version 0.8.44 does not change public API, valid Protocol v2 framing, method/field IDs, or payload layouts. Application code does not require migration.

Client, static endpoint-cluster, and Server shutdown now preserve unexpected background/session failures that an expected sibling connection close previously hid. Code that relied on Stop silently ignoring an internal failure from a custom transport, resolver, or callback should correct that source. Ordinary cancellation, connection closure, and initial connection failures already observed by the `ConnectAsync` caller retain their previous behavior.

When a bounded send queue rejects a terminal response or terminal stream frame, the original exception type and error code are unchanged, but Server call admission, service/request ownership, and the stream flow-control slot are always released. This is a lifecycle correction and does not alter successful calls, valid flow-control credits, or wire bytes.
