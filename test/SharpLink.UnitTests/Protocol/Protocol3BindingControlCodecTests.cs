using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

// These tests exercise the production scalar codecs. They do not model dispatch or
// claim Session lifecycle coverage: no business callback/provider is supplied to a codec.
public class Protocol3BindingControlCodecTests
{
    private static readonly Guid ServerId = new("00112233-4455-6677-8899-aabbccddeeff");
    private static readonly Guid Epoch = new("10213243-5465-7687-98a9-bacbdcedfe0f");
    private static readonly RpcHash128 Hash = new(0x0807060504030201, 0x1817161514131211);
    private static readonly Protocol3BindingAuthority Authority = new(ServerId, Epoch, Protocol3BindingLimits.Default);
    private static readonly Protocol3BindingReceipt Receipt = new(ServerId, Epoch, 7, 11);
    private static readonly Protocol3BindRequest Request = new(Protocol3BindingRequirement.Compatible, -2, Hash);
    private static readonly Protocol3BindResult Success = new(Protocol3BindStatus.Success, (3UL << 16) | 4,
        -2, Hash, 11, 7, ServerId, Epoch);

    [Test]
    public void BindRequestHasCanonicalLengthsByteOrderAndReceiptLayout()
    {
        var bytes = Encode(writer => Protocol3BindingControlCodec.WriteBindContract(writer, Request));
        Ensure(bytes.AsSpan().SequenceEqual(Convert.FromHexString(
            "00000000FEFFFFFFFFFFFFFF01020304050607081112131415161718")));
        Ensure(Protocol3BindingControlCodec.ReadBindContract(new(bytes)) == Request);
        foreach (var requirement in new[] { Protocol3BindingRequirement.Compatible,
                     Protocol3BindingRequirement.MinimumVersion, Protocol3BindingRequirement.ExactRegistration })
        {
            var request = Request with { Requirement = requirement, Receipt = Receipt };
            var encoded = Encode(writer => Protocol3BindingControlCodec.WriteBindContract(writer, request));
            Ensure(encoded.Length == 76 && encoded[0] == (byte)requirement && encoded[1] == 1);
            Ensure(encoded.AsSpan(28, 16).SequenceEqual(Convert.FromHexString("33221100554477668899AABBCCDDEEFF")));
            Ensure(encoded.AsSpan(44, 16).SequenceEqual(Convert.FromHexString("433221106554877698A9BACBDCEDFE0F")));
            Ensure(BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(60)) == 7);
            Ensure(BinaryPrimitives.ReadUInt64LittleEndian(encoded.AsSpan(68)) == 11);
            for (var split = 1; split < encoded.Length; split++)
                Ensure(Protocol3BindingControlCodec.ReadBindContract(Split(encoded, split)) == request);
        }
    }

    [Test]
    public void BindRequestRejectsNoncanonicalRequirementReceiptReservedAndIdentity()
    {
        var plain = Encode(writer => Protocol3BindingControlCodec.WriteBindContract(writer, Request));
        foreach (var pair in new[] { (0, (byte)1), (0, (byte)2), (0, (byte)3), (0, (byte)255),
                     (1, (byte)1), (1, (byte)2), (2, (byte)1), (3, (byte)1) })
            RejectMutation(plain, pair.Item1, pair.Item2, bytes => Protocol3BindingControlCodec.ReadBindContract(new(bytes)));
        RejectCleared(plain, 4, 8, bytes => Protocol3BindingControlCodec.ReadBindContract(new(bytes)));
        RejectCleared(plain, 12, 16, bytes => Protocol3BindingControlCodec.ReadBindContract(new(bytes)));

        var withReceipt = Request with { Receipt = Receipt };
        var full = Encode(writer => Protocol3BindingControlCodec.WriteBindContract(writer, withReceipt));
        RejectMutation(full, 1, 0, bytes => Protocol3BindingControlCodec.ReadBindContract(new(bytes)));
        RejectCleared(full, 28, 16, bytes => Protocol3BindingControlCodec.ReadBindContract(new(bytes)));
        RejectCleared(full, 44, 16, bytes => Protocol3BindingControlCodec.ReadBindContract(new(bytes)));
        foreach (var offset in new[] { 60, 68 })
        {
            foreach (var invalid in new[] { 0UL, 1UL << 63, ulong.MaxValue })
            {
                var copy = (byte[])full.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(offset), invalid);
                ExpectViolation(() => Protocol3BindingControlCodec.ReadBindContract(new(copy)));
            }
        }
        foreach (var value in new[] { 1UL, (ulong)long.MaxValue })
        {
            var request = Request with { Receipt = Receipt with { Generation = value, RegistrationId = value } };
            var encoded = Encode(writer => Protocol3BindingControlCodec.WriteBindContract(writer, request));
            Ensure(Protocol3BindingControlCodec.ReadBindContract(new(encoded)) == request);
        }
    }

    [Test]
    public void BindResultSuccessHasExactIdentityOffsetsAndSegmentedRoundTrip()
    {
        var bytes = Encode(writer => Protocol3BindingControlCodec.WriteBindResult(writer, Success, 4096));
        Ensure(bytes.Length == 84);
        Ensure(bytes.AsSpan(0, 12).SequenceEqual(Convert.FromHexString("000000000400030000000000")));
        Ensure(BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(12)) == -2);
        Ensure(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(20)) == Hash.High);
        Ensure(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(28)) == Hash.Low);
        Ensure(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(36)) == 11);
        Ensure(BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(44)) == 7);
        Ensure(bytes.AsSpan(52, 16).SequenceEqual(Convert.FromHexString("33221100554477668899AABBCCDDEEFF")));
        Ensure(bytes.AsSpan(68, 16).SequenceEqual(Convert.FromHexString("433221106554877698A9BACBDCEDFE0F")));
        for (var split = 1; split < bytes.Length; split++)
            Ensure(Protocol3BindingControlCodec.ReadBindResult(Split(bytes, split), 4096) == Success);

        foreach (var offset in new[] { 36, 44 })
        {
            foreach (var invalid in new[] { 0UL, 1UL << 63, ulong.MaxValue })
            {
                var copy = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(offset), invalid);
                ExpectViolation(() => Protocol3BindingControlCodec.ReadBindResult(new(copy), 4096));
            }
        }
        foreach (var range in new[] { (4, 8), (12, 8), (20, 16), (52, 16), (68, 16) })
            RejectCleared(bytes, range.Item1, range.Item2, value => Protocol3BindingControlCodec.ReadBindResult(new(value), 4096));
        foreach (var offset in new[] { 2, 3 })
            RejectMutation(bytes, offset, 1, value => Protocol3BindingControlCodec.ReadBindResult(new(value), 4096));
        var maximum = Success with { Generation = long.MaxValue, RegistrationId = long.MaxValue, Token = ulong.MaxValue };
        var maxBytes = Encode(writer => Protocol3BindingControlCodec.WriteBindResult(writer, maximum, 65536));
        Ensure(Protocol3BindingControlCodec.ReadBindResult(new(maxBytes), 65536) == maximum);
    }

    [Test]
    public void FailureStatusesRequireEveryIdentityByteZero()
    {
        for (var status = 1; status <= 12; status++)
        {
            var failure = Protocol3BindResult.Failure((Protocol3BindStatus)status);
            var bytes = Encode(writer => Protocol3BindingControlCodec.WriteBindResult(writer, failure, 4096));
            Ensure(Protocol3BindingControlCodec.ReadBindResult(new(bytes), 4096) == failure);
            for (var offset = 4; offset < 84; offset++)
                RejectMutation(bytes, offset, 1, value => Protocol3BindingControlCodec.ReadBindResult(new(value), 4096));
        }
        foreach (var status in new ushort[] { 13, ushort.MaxValue })
        {
            var bytes = new byte[84];
            BinaryPrimitives.WriteUInt16LittleEndian(bytes, status);
            ExpectViolation(() => Protocol3BindingControlCodec.ReadBindResult(new(bytes), 4096));
        }
    }

    [Test]
    public void SuccessValidationPreservesReceiptRequirementAndSessionAuthority()
    {
        Protocol3BindingValidation.ValidateResultForRequest(Success, Request, Authority, 4096);
        var compatible = Request with { Receipt = Receipt };
        var different = Success with { RegistrationId = 12, Generation = 1 };
        Protocol3BindingValidation.ValidateResultForRequest(different, compatible, Authority, 4096);
        var minimum = compatible with { Requirement = Protocol3BindingRequirement.MinimumVersion };
        ExpectViolation(() => Protocol3BindingValidation.ValidateResultForRequest(different, minimum, Authority, 4096));
        different = different with { Generation = 8 };
        Protocol3BindingValidation.ValidateResultForRequest(different, minimum, Authority, 4096);
        var exact = compatible with { Requirement = Protocol3BindingRequirement.ExactRegistration };
        ExpectViolation(() => Protocol3BindingValidation.ValidateResultForRequest(different, exact, Authority, 4096));
        Protocol3BindingValidation.ValidateResultForRequest(Success, exact, Authority, 4096);
        var olderExact = Success with { Generation = 6 };
        ExpectViolation(() => Protocol3BindingValidation.ValidateResultForRequest(olderExact, exact, Authority, 4096));
        foreach (var invalid in new[] { Success with { ContractId = 5 }, Success with { Hash = new RpcHash128(1, 0) },
                     Success with { ServerInstanceId = Epoch }, Success with { RegistryEpoch = ServerId } })
            ExpectViolation(() => Protocol3BindingValidation.ValidateResultForRequest(invalid, Request, Authority, 4096));
        var foreign = compatible with { Receipt = Receipt with { RegistryEpoch = ServerId } };
        ExpectViolation(() => Protocol3BindingValidation.ValidateResultForRequest(Success, foreign, Authority, 4096));
    }

    [Test]
    public void TokenLimitsInvalidateReleaseAndCancelAreStrict()
    {
        Ensure(Protocol3BindingToken.Create(0, 1, 1) == 65536);
        Ensure(Protocol3BindingToken.Create(ushort.MaxValue, Protocol3BindingToken.MaximumIncarnation, 65536) == ulong.MaxValue);
        Ensure(Protocol3BindingToken.Slot(ulong.MaxValue) == 65535);
        Ensure(Protocol3BindingToken.Incarnation(ulong.MaxValue) == Protocol3BindingToken.MaximumIncarnation);
        ExpectArgument(() => Protocol3BindingToken.Create(0, 0, 1));
        ExpectArgument(() => Protocol3BindingToken.Create(0, 1UL << 48, 1));
        ExpectArgument(() => Protocol3BindingToken.Create(1, 1, 1));
        foreach (var reason in new[] { Protocol3BindingInvalidationReason.RegistrationRetired,
                     Protocol3BindingInvalidationReason.Evicted, Protocol3BindingInvalidationReason.SessionDraining })
        {
            var value = new Protocol3BindingInvalidated(Success.Token, reason);
            var bytes = Encode(writer => Protocol3BindingControlCodec.WriteBindingInvalidated(writer, value, 4096));
            Ensure(bytes.Length == 12 && BinaryPrimitives.ReadUInt64LittleEndian(bytes) == Success.Token);
            Ensure(BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) == (ushort)reason);
            Ensure(Protocol3BindingControlCodec.ReadBindingInvalidated(Split(bytes, 7), 4096) == value);
            foreach (var offset in new[] { 10, 11 })
                RejectMutation(bytes, offset, 1, data => Protocol3BindingControlCodec.ReadBindingInvalidated(new(data), 4096));
            foreach (var invalid in new ushort[] { 0, 4, ushort.MaxValue })
            {
                var copy = (byte[])bytes.Clone();
                BinaryPrimitives.WriteUInt16LittleEndian(copy.AsSpan(8), invalid);
                ExpectViolation(() => Protocol3BindingControlCodec.ReadBindingInvalidated(new(copy), 4096));
            }
        }
        var release = Encode(writer => Protocol3BindingControlCodec.WriteReleaseBinding(writer, Success.Token, 4096));
        Ensure(Protocol3BindingControlCodec.ReadReleaseBinding(Split(release, 3), 4096) == Success.Token);
        foreach (var token in new[] { 0UL, 65535UL, (1UL << 16) | 4096 })
        {
            var bytes = new byte[12];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, token);
            bytes[8] = 1;
            ExpectViolation(() => Protocol3BindingControlCodec.ReadBindingInvalidated(new(bytes), 4096));
            ExpectViolation(() => Protocol3BindingControlCodec.ReadReleaseBinding(new(bytes.AsMemory(0, 8)), 4096));
            var result = Success with { Token = token };
            Ensure(!Protocol3BindingValidation.IsValid(result, 4096));
        }
        foreach (var id in new[] { 1UL, (ulong)long.MaxValue })
        {
            var empty = Encode(writer => Protocol3BindingControlCodec.WriteCancelBind(writer, id));
            Ensure(empty.Length == 0);
            Protocol3BindingControlCodec.ReadCancelBind(new(empty), id);
        }
        foreach (var id in new[] { 0UL, 1UL << 63, ulong.MaxValue })
        {
            ExpectViolation(() => Protocol3BindingControlCodec.ReadCancelBind(ReadOnlySequence<byte>.Empty, id));
            ExpectViolation(() => Protocol3BindingValidation.ValidateOperationId(id));
        }
        ExpectViolation(() => Protocol3BindingControlCodec.ReadCancelBind(new(new byte[1]), 1));
    }

    [Test]
    public void EveryControlReaderRejectsTruncatedAndTrailingBytes()
    {
        for (var length = 0; length <= 85; length++)
        {
            var payload = new ReadOnlySequence<byte>(new byte[length]);
            if (length != 28 && length != 76)
                ExpectViolation(() => Protocol3BindingControlCodec.ReadBindContract(payload));
            if (length != 84)
                ExpectViolation(() => Protocol3BindingControlCodec.ReadBindResult(payload, 4096));
            if (length != 12)
                ExpectViolation(() => Protocol3BindingControlCodec.ReadBindingInvalidated(payload, 4096));
            if (length != 8)
                ExpectViolation(() => Protocol3BindingControlCodec.ReadReleaseBinding(payload, 4096));
            if (length != 0)
                ExpectViolation(() => Protocol3BindingControlCodec.ReadCancelBind(payload, 1));
        }
    }

    [Test]
    public void InvalidLocalValuesAreRejectedBeforeWriterAdvances()
    {
        var writer = new ArrayBufferWriter<byte>();
        ExpectArgument(() => Protocol3BindingControlCodec.WriteBindContract(writer, Request with { ContractId = 0 }));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteBindContract(writer, Request with { Requirement = Protocol3BindingRequirement.MinimumVersion }));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteBindContract(writer, Request with { Receipt = Receipt with { Generation = 1UL << 63 } }));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteBindResult(writer, Success with { Generation = 0 }, 4096));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteBindResult(writer, Success with { Status = Protocol3BindStatus.HashMismatch }, 4096));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteBindingInvalidated(writer, new(Success.Token, 0), 4096));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteReleaseBinding(writer, 0, 4096));
        ExpectArgument(() => Protocol3BindingControlCodec.WriteCancelBind(writer, 0));
        Ensure(writer.WrittenCount == 0);
    }

    private static byte[] Encode(Action<IBufferWriter<byte>> write)
    {
        var writer = new ArrayBufferWriter<byte>();
        write(writer);
        return writer.WrittenSpan.ToArray();
    }

    private static void RejectMutation(byte[] source, int offset, byte value, Action<byte[]> read)
    {
        var bytes = (byte[])source.Clone();
        bytes[offset] = value;
        ExpectViolation(() => read(bytes));
    }

    private static void RejectCleared(byte[] source, int offset, int length, Action<byte[]> read)
    {
        var bytes = (byte[])source.Clone();
        bytes.AsSpan(offset, length).Clear();
        ExpectViolation(() => read(bytes));
    }

    private static void Ensure(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Binding control codec assertion failed.");
    }

    private static void ExpectViolation(Action action)
    {
        try { action(); }
        catch (SharpLinkProtocolViolationException error) when (error.Code == SharpLinkErrorCode.ProtocolViolation) { return; }
        throw new InvalidOperationException("Expected a binding protocol violation.");
    }

    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected invalid local binding input.");
    }

    private static ReadOnlySequence<byte> Split(byte[] bytes, int offset)
    {
        var first = new Segment(bytes.AsMemory(0, offset));
        var last = first.Append(bytes.AsMemory(offset));
        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        internal Segment(ReadOnlyMemory<byte> memory) => Memory = memory;
        internal Segment Append(ReadOnlyMemory<byte> memory)
        {
            var next = new Segment(memory) { RunningIndex = RunningIndex + Memory.Length };
            Next = next;
            return next;
        }
    }
}
