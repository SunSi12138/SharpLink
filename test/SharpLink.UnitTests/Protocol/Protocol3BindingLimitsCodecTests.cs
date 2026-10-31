using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

public class Protocol3BindingLimitsCodecTests
{
    [Test]
    public void LimitsAndAuthorityHavePinnedLittleEndianBytes()
    {
        var bytes = new byte[16];
        Protocol3BindingLimitsCodec.Write(bytes, Protocol3BindingLimits.Default);
        Ensure(bytes.AsSpan().SequenceEqual(Convert.FromHexString("0010000040007F003075000000010000")));
        Ensure(Protocol3BindingLimitsCodec.Read(bytes) == Protocol3BindingLimits.Default);

        var authority = new Protocol3BindingAuthority(
            new Guid("00112233-4455-6677-8899-aabbccddeeff"),
            new Guid("10213243-5465-7687-98a9-bacbdcedfe0f"), Protocol3BindingLimits.Default);
        var encoded = new byte[48];
        Protocol3BindingLimitsCodec.WriteAuthority(encoded, authority);
        Ensure(encoded.AsSpan(0, 16).SequenceEqual(Convert.FromHexString("33221100554477668899AABBCCDDEEFF")));
        Ensure(encoded.AsSpan(16, 16).SequenceEqual(Convert.FromHexString("433221106554877698A9BACBDCEDFE0F")));
        Ensure(encoded.AsSpan(32).SequenceEqual(bytes));
        Ensure(Protocol3BindingLimitsCodec.ReadAuthority(encoded) == authority);
    }

    [Test]
    public void LimitsRejectEveryZeroAndOverCeilingWithoutClamping()
    {
        var valid = new Protocol3BindingLimits(65536, 64, 127, 60000, 256);
        var bytes = new byte[16];
        Protocol3BindingLimitsCodec.Write(bytes, valid);
        Ensure(Protocol3BindingLimitsCodec.Read(bytes) == valid);
        var invalid = new[]
        {
            valid with { Slots = 0 }, valid with { Slots = 65537 },
            valid with { Operations = 0 }, valid with { Operations = 65 },
            valid with { MaxClientStreamsPerCall = 0 }, valid with { MaxClientStreamsPerCall = 128 },
            valid with { OperationDeadlineMs = 0 }, valid with { OperationDeadlineMs = 60001 },
            valid with { QueuedControlFrames = 0 }, valid with { QueuedControlFrames = 257 }
        };
        foreach (var value in invalid)
        {
            ExpectArgument(() => Protocol3BindingLimitsCodec.Write(bytes, value));
            // Deliberately encode malformed wire without invoking the production writer.
            BinaryPrimitives.WriteUInt32LittleEndian(bytes, value.Slots);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), value.Operations);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(6), value.MaxClientStreamsPerCall);
            BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), value.OperationDeadlineMs);
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(12), value.QueuedControlFrames);
            ExpectViolation(() => Protocol3BindingLimitsCodec.Read(bytes));
        }
    }

    [Test]
    public void FixedLengthsReservedFieldsAndEmptyAuthorityAreRejected()
    {
        for (var length = 0; length <= 49; length++)
        {
            var malformed = new byte[length];
            if (length != 16)
                ExpectViolation(() => Protocol3BindingLimitsCodec.Read(malformed));
            if (length != 48)
                ExpectViolation(() => Protocol3BindingLimitsCodec.ReadAuthority(malformed));
        }
        for (var offset = 14; offset < 16; offset++)
        {
            var bytes = new byte[16];
            Protocol3BindingLimitsCodec.Write(bytes, Protocol3BindingLimits.Default);
            bytes[offset] = 1;
            ExpectViolation(() => Protocol3BindingLimitsCodec.Read(bytes));
        }
        var authority = new Protocol3BindingAuthority(Guid.NewGuid(), Guid.NewGuid(), Protocol3BindingLimits.Default);
        foreach (var offset in new[] { 0, 16 })
        {
            var bytes = new byte[48];
            Protocol3BindingLimitsCodec.WriteAuthority(bytes, authority);
            bytes.AsSpan(offset, 16).Clear();
            ExpectViolation(() => Protocol3BindingLimitsCodec.ReadAuthority(bytes));
        }
        ExpectArgument(() => Protocol3BindingLimitsCodec.WriteAuthority(new byte[48], authority with { ServerInstanceId = Guid.Empty }));
        ExpectArgument(() => Protocol3BindingLimitsCodec.WriteAuthority(new byte[48], authority with { RegistryEpoch = Guid.Empty }));
    }

    [Test]
    public void NegotiationIsElementwiseMinimumAndNeverAcceptsLargerResponse()
    {
        var offer = new Protocol3BindingLimits(100, 20, 60, 1000, 70);
        var local = new Protocol3BindingLimits(200, 10, 30, 2000, 80);
        var chosen = Protocol3BindingLimitsCodec.Negotiate(offer, local);
        Ensure(chosen == new Protocol3BindingLimits(100, 10, 30, 1000, 70));
        Protocol3BindingLimitsCodec.ValidateNegotiated(chosen, offer, local);
        foreach (var invalid in new[]
        {
            chosen with { Slots = 101 }, chosen with { Operations = 11 },
            chosen with { MaxClientStreamsPerCall = 31 }, chosen with { OperationDeadlineMs = 1001 },
            chosen with { QueuedControlFrames = 71 }, default
        })
            ExpectViolation(() => Protocol3BindingLimitsCodec.ValidateNegotiated(invalid, offer, local));
        ExpectViolation(() => Protocol3BindingLimitsCodec.Negotiate(default, local));
        ExpectArgument(() => Protocol3BindingLimitsCodec.Negotiate(offer, default));
    }

    private static void Ensure(bool condition)
    {
        if (!condition)
            throw new InvalidOperationException("Binding limits codec assertion failed.");
    }

    private static void ExpectViolation(Action action)
    {
        try { action(); }
        catch (SharpLinkProtocolViolationException error) when (error.Reason == ProtocolViolationReason.MalformedFrame) { return; }
        throw new InvalidOperationException("Expected malformed binding limits.");
    }

    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Expected invalid local binding limits.");
    }
}
