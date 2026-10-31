using System.Buffers.Binary;

namespace SharpLink.UnitTests.BindingPrototype;

// ISOLATED TEST MODEL. Not a production codec, negotiated mode, public API or full S1 implementation.
// Variable legacy handshake fields, business metadata/compression and transport are deliberately absent.
internal static class BindingScalarCodec
{
    internal enum Phase { Handshaking, Ready, Draining, Terminal }
    internal enum Requirement : byte { Compatible, MinimumVersion, ExactRegistration }
    internal enum Status : ushort
    {
        Success, UnknownContract, HashMismatch, ReceiptScopeMismatch, BelowMinimum,
        RegistrationMismatch, RegistrationRetiring, BindingCapacity, ControlCapacity,
        ControlDeadline, ScopeDenied, CancelledBeforeCommit, SessionDraining
    }
    internal sealed class ModelViolation(string reason) : Exception(reason);
    internal static void Need(bool condition, string reason)
    {
        if (!condition) throw new ModelViolation(reason);
    }

    internal readonly record struct Limits(uint Slots, ushort Operations, ushort Streams,
        uint DeadlineMs, ushort QueuedFrames)
    {
        internal static Limits Default => new(4096, 64, 127, 30000, 256);
        internal void Validate() => Need(Slots is >= 1 and <= 65536 && Operations is >= 1 and <= 64 &&
            Streams is >= 1 and <= 127 && DeadlineMs is >= 1 and <= 60000 &&
            QueuedFrames is >= 1 and <= 256, "limits");
    }

    internal readonly record struct Key(long Contract, ulong HashHigh, ulong HashLow,
        Requirement Requirement, bool HasReceipt, Guid Server, Guid Epoch, ulong Generation, ulong Registration)
    {
        internal void Validate()
        {
            Need((HashHigh | HashLow) != 0 && (byte)Requirement <= 2, "identity");
            Need(HasReceipt || Requirement == Requirement.Compatible, "receipt_required");
            Need(HasReceipt
                ? Server != Guid.Empty && Epoch != Guid.Empty && Generation != 0 && Registration != 0
                : Server == Guid.Empty && Epoch == Guid.Empty && Generation == 0 && Registration == 0,
                "receipt_canonical");
        }
    }

    internal readonly record struct Result(Status Status, ulong Token, long Contract, ulong HashHigh,
        ulong HashLow, ulong Registration, ulong Generation, Guid Server, Guid Epoch)
    {
        internal static Result Failure(Status status) => new(status, 0, 0, 0, 0, 0, 0, Guid.Empty, Guid.Empty);
        internal void Validate()
        {
            Need((ushort)Status <= 12, "status");
            if (Status == Status.Success)
                Need(Token != 0 && (Token >> 16) != 0 && (HashHigh | HashLow) != 0 &&
                    Registration != 0 && Generation != 0 && Server != Guid.Empty && Epoch != Guid.Empty, "success");
            else
                Need(Token == 0 && Contract == 0 && HashHigh == 0 && HashLow == 0 && Registration == 0 &&
                    Generation == 0 && Server == Guid.Empty && Epoch == Guid.Empty, "failure_body");
        }
    }

    internal static byte[] WriteLimits(Limits value)
    {
        value.Validate();
        var bytes = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value.Slots);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), value.Operations);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), value.Streams);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value.DeadlineMs);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), value.QueuedFrames);
        return bytes;
    }

    internal static Limits ReadLimits(ReadOnlySpan<byte> bytes)
    {
        Need(bytes.Length == 16, "limits_length");
        Need(BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]) == 0, "reserved");
        var value = new Limits(BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]), BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]));
        value.Validate();
        return value;
    }

    internal static byte[] WriteAuthority(Guid server, Guid epoch, Limits limits)
    {
        Need(server != Guid.Empty && epoch != Guid.Empty, "authority");
        var bytes = new byte[48];
        server.TryWriteBytes(bytes.AsSpan(0, 16));
        epoch.TryWriteBytes(bytes.AsSpan(16, 16));
        WriteLimits(limits).CopyTo(bytes, 32);
        return bytes;
    }

    internal static (Guid Server, Guid Epoch, Limits Limits) ReadAuthority(ReadOnlySpan<byte> bytes)
    {
        Need(bytes.Length == 48, "authority_length");
        var server = new Guid(bytes[..16]);
        var epoch = new Guid(bytes.Slice(16, 16));
        Need(server != Guid.Empty && epoch != Guid.Empty, "authority");
        return (server, epoch, ReadLimits(bytes[32..]));
    }

    internal static byte[] WriteBind(Key value)
    {
        value.Validate();
        var bytes = new byte[value.HasReceipt ? 76 : 28];
        bytes[0] = (byte)value.Requirement;
        bytes[1] = value.HasReceipt ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(4), value.Contract);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(12), value.HashHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), value.HashLow);
        if (value.HasReceipt)
        {
            value.Server.TryWriteBytes(bytes.AsSpan(28, 16));
            value.Epoch.TryWriteBytes(bytes.AsSpan(44, 16));
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(60), value.Generation);
            BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(68), value.Registration);
        }
        return bytes;
    }

    internal static Key ReadBind(ReadOnlySpan<byte> bytes)
    {
        Need(bytes.Length is 28 or 76, "bind_length");
        Need(bytes[1] <= 1 && BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) == 0, "reserved");
        var receipt = bytes[1] == 1;
        Need(bytes.Length == (receipt ? 76 : 28), "bind_length");
        var key = new Key(BinaryPrimitives.ReadInt64LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[12..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]),
            (Requirement)bytes[0], receipt, receipt ? new Guid(bytes.Slice(28, 16)) : Guid.Empty,
            receipt ? new Guid(bytes.Slice(44, 16)) : Guid.Empty,
            receipt ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[60..]) : 0,
            receipt ? BinaryPrimitives.ReadUInt64LittleEndian(bytes[68..]) : 0);
        key.Validate();
        return key;
    }

    internal static byte[] WriteResult(Result value)
    {
        value.Validate();
        var bytes = new byte[84];
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)value.Status);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(4), value.Token);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(12), value.Contract);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(20), value.HashHigh);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(28), value.HashLow);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(36), value.Registration);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(44), value.Generation);
        value.Server.TryWriteBytes(bytes.AsSpan(52, 16));
        value.Epoch.TryWriteBytes(bytes.AsSpan(68, 16));
        return bytes;
    }

    internal static Result ReadResult(ReadOnlySpan<byte> bytes)
    {
        Need(bytes.Length == 84, "result_length");
        Need(BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]) == 0, "reserved");
        var result = new Result((Status)BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[4..]), BinaryPrimitives.ReadInt64LittleEndian(bytes[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[20..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[28..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[36..]), BinaryPrimitives.ReadUInt64LittleEndian(bytes[44..]),
            new Guid(bytes.Slice(52, 16)), new Guid(bytes.Slice(68, 16)));
        result.Validate();
        return result;
    }

    // Only the five binding-control frames are modeled, not the production general frame parser.
    internal static byte[] WriteFrame(byte type, ulong id, ReadOnlySpan<byte> payload)
    {
        ValidateControl(type, id, payload);
        var bytes = new byte[15 + payload.Length];
        bytes[0] = 0x89;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), payload.Length);
        bytes[5] = type;
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(7), id);
        payload.CopyTo(bytes.AsSpan(15));
        return bytes;
    }

    internal static (byte Type, ulong Id) ReadFrame(ReadOnlySpan<byte> bytes, bool receiverIsServer, Phase phase)
    {
        Need(bytes.Length >= 15 && bytes[0] == 0x89, "header");
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        Need(length >= 0 && length <= 84 && bytes.Length - 15 == length, "frame_length");
        Need(bytes[6] == 0, "flags");
        var type = bytes[5];
        var id = BinaryPrimitives.ReadUInt64LittleEndian(bytes[7..]);
        Need(phase is Phase.Ready or Phase.Draining, "phase");
        Need(receiverIsServer ? type is 17 or 20 or 21 : type is 18 or 19, "direction");
        ValidateControl(type, id, bytes[15..]);
        return (type, id);
    }

    private static void ValidateControl(byte type, ulong id, ReadOnlySpan<byte> payload)
    {
        Need(type is >= 17 and <= 21, "type");
        Need(type is 19 or 20 ? id == 0 : id != 0, "id_domain");
        switch (type)
        {
            case 17: ReadBind(payload); break;
            case 18: ReadResult(payload); break;
            case 19:
                Need(payload.Length == 12, "invalidate_length");
                Need(BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) is >= 1 and <= 3 &&
                    BinaryPrimitives.ReadUInt16LittleEndian(payload[10..]) == 0, "invalidate_reason");
                Need(BinaryPrimitives.ReadUInt64LittleEndian(payload) >> 16 != 0, "token");
                break;
            case 20:
                Need(payload.Length == 8 && BinaryPrimitives.ReadUInt64LittleEndian(payload) >> 16 != 0, "release");
                break;
            case 21: Need(payload.IsEmpty, "cancel_length"); break;
        }
    }
}
