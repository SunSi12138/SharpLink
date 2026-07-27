# 0.8.31 regression-test research

## Bounded target inventory

- Custom socket endpoint ownership: `SocketTransportSocketFactory.Snapshot` clones the three built-in endpoint types but returns every other `EndPoint` by reference. A mutable custom endpoint can therefore change a factory's connection target after construction. The `EndPoint.Serialize`/`Create` contract provides a general snapshot path.
- Unix-domain socket cleanup: the listener remembers only a filesystem path. If that socket node is unlinked and replaced while the listener is alive, disposal deletes the replacement even though its device/inode no longer belongs to the listener.
- Duplicate raw framing surface: public `ProtocolV2FrameWriter`/`ProtocolV2FrameToken` can silently backfill writer B with a token from writer A, while the framework already uses the separate internal packet writer on every generated hot path. Runtime identity checks measurably regress this nanosecond-scale API, so the duplicate raw writer belongs inside the implementation boundary.
- Anonymous-pipe handle transfer: the BCL requires `AnonymousPipeServerStream.DisposeLocalCopyOfClientHandle` after inherited handles reach a child process; otherwise the server cannot observe client disposal. `AnonymousPipeOffer` exposes no completion hook and its generated record `ToString` prints both inheritable handles.
- Obsolete public abstractions: `GeneratedProxyRegistry` and `GeneratedStubRegistry` are no longer emitted or consumed but retain process-wide strong `Type`/delegate roots. `ISerializer`, `IServiceRegister`, and `StripedLongSet` have zero repository consumers; `StripedLongMap`, `RpcBufferWriterExtensions`, `PacketToken`, and `PacketScope` are implementation-only despite being exported.

## Evidence and engineering boundary

- The anonymous-pipe requirement is explicit in the official .NET API contract: after transfer, the parent must close its local client-handle copy or it will not receive client-disposal notification. SharpLink will expose that same explicit transfer-completion lifecycle rather than guessing when an external child inherited the handles.
- Unix socket ownership will be identified with the runtime's cross-Unix `System.Native` `lstat` shim (file type, device, inode), matching the stable `FileStatus` ABI used by .NET itself. Cleanup remains unchanged on Windows and abstract Unix sockets remain filesystem-free.
- The internal packet writer stays allocation-free and raw for generated/runtime hot paths. The duplicate raw protocol writer/token are made internal instead of adding identity checks or a header-validation switch to every frame.
- The API cleanup is limited to symbols with no current generator/runtime/documentation consumer. Supported manifest catalogs, codec adapters, sessions, stream dispatchers, and transport contracts remain public.

## Acceptance checklist

- A custom mutable endpoint is cloned through `Create(Serialize())` and later source mutations cannot alter the snapshot.
- Listener disposal deletes its own unchanged Unix socket node but preserves any path replacement.
- Raw frame/packet writers and their caller-forgeable offset tokens are no longer exported as supported public abstractions.
- Anonymous-pipe offers redact handles and provide idempotent completion of the parent-side handle transfer.
- Dead registries/interfaces/set disappear, while implementation-only collection and packet helpers are no longer exported.
- Existing behavior and runtime hot-path performance remain stable.

## Rejected/deferred hypotheses

Full outbound frame-header validation would add work to every generated frame for misuse that is already caught by the receiving parser, so it is not bundled with the concrete cross-writer corruption fix. Shared-memory `PipeReader` contract misuse and multi-cluster deferred-unregister polling remain low-value/internal hypotheses without a production witness.
