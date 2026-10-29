namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private sealed partial class SendPump
    {
        // This is only a serialization optimization of an already selected batch. Never
        // wait for another update, inspect another queue item, or retire an original owner.
        private void WriteCoalescedWindowUpdateBatch(List<OwnedFrame> pending)
        {
            var length = 0;
            for (var index = 0; index < pending.Count; index++)
                length = checked(length + pending[index].Length);
            if (length == 0)
                return;

            var destination = _output.GetSpan(length);
            var offset = 0;
            var creditOffset = -1;
            ulong previousRequestId = 0;
            ushort previousStreamId = 0;
            var previousCredit = 0;
            for (var index = 0; index < pending.Count; index++)
            {
                var frame = pending[index];
                var eligible = TryReadCoalescibleWindowUpdate(
                    frame, out var requestId, out var streamId, out var credit);
                if (eligible && creditOffset >= 0 &&
                    requestId == previousRequestId && streamId == previousStreamId &&
                    credit <= int.MaxValue - previousCredit)
                {
                    previousCredit += credit;
                    BinaryPrimitives.WriteInt32LittleEndian(
                        destination.Slice(creditOffset, sizeof(int)), previousCredit);
                    continue;
                }

                // Empty fences and ineligible/malformed frames are boundaries too. Preserve
                // their original bytes and remote validation rather than hiding or throwing
                // on them here. An overflowing sum starts a new, individually valid group.
                creditOffset = eligible
                    ? offset + ProtocolV2Constants.HeaderBytes + sizeof(ushort)
                    : -1;
                previousRequestId = requestId;
                previousStreamId = streamId;
                previousCredit = credit;
                frame.Memory.Span.CopyTo(destination[offset..]);
                offset += frame.Length;
            }
            _output.Advance(offset);
            SharpLinkTelemetry.RecordSentBytes(offset);
        }

        private static bool TryReadCoalescibleWindowUpdate(
            in OwnedFrame frame,
            out ulong requestId,
            out ushort streamId,
            out int credit)
        {
            requestId = 0;
            streamId = 0;
            credit = 0;
            const int payloadBytes = sizeof(ushort) + sizeof(uint);
            const int frameBytes = ProtocolV2Constants.HeaderBytes + payloadBytes;
            if (!frame.IsCoalescibleWindowUpdate || frame.ForceFlush ||
                frame.FlushCompletion is not null || frame.FailureObserver is not null ||
                frame.Length != frameBytes || frame.Memory.Length != frameBytes)
                return false;

            var bytes = frame.Memory.Span;
            if (bytes[0] != ProtocolV2Constants.Magic ||
                BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(1, sizeof(int))) != payloadBytes ||
                bytes[5] != (byte)ProtocolV2FrameType.WindowUpdate ||
                bytes[6] != (byte)ProtocolV2FrameFlags.None)
                return false;

            requestId = BinaryPrimitives.ReadUInt64LittleEndian(bytes.Slice(7, sizeof(ulong)));
            streamId = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.Slice(ProtocolV2Constants.HeaderBytes, sizeof(ushort)));
            credit = BinaryPrimitives.ReadInt32LittleEndian(
                bytes.Slice(ProtocolV2Constants.HeaderBytes + sizeof(ushort), sizeof(int)));
            return requestId != 0 && credit > 0;
        }
    }
}
