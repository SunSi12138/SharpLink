# SharpLink 0.8.28 migration guide

Chinese: [`../migration-0.8.28.md`](../migration-0.8.28.md)

0.8.28 does not change Protocol v2 framing, valid error payloads, or generated code. It only rejects unusable or unreadable inputs earlier.

- `SocketTransportOptions.KeepAliveTime` and `KeepAliveInterval` must be positive and no greater than 2,147,483,647 seconds.
- Token-bucket replenishment and fixed/sliding windows must be positive and no greater than 2,147,483,647 milliseconds.
- Sliding-window `SegmentsPerWindow` remains at least two and cannot exceed `Window.Ticks`.
- Named-pipe factories/listeners accept only their supported `PipeOptions` bits, clients reject the server-only `FirstPipeInstance`, and listeners require a defined `PipeTransmissionMode`. Normal .NET platform restrictions still apply to otherwise valid modes.
- Direct `ProtocolV2PayloadCodec.WriteError` calls require a defined `SharpLinkErrorCode`; invalid values throw `ArgumentOutOfRangeException` before mutating the writer.

Defaults already satisfy every bound. Replace `TimeSpan.MaxValue` rate windows with explicit rule disablement; an enormous finite timer is not a reliable disable mechanism.
