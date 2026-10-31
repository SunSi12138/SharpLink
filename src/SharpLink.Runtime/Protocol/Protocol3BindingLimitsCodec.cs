namespace SharpLink.Runtime;

internal static class Protocol3BindingLimitsCodec
{
    internal const int EncodedBytes = 16;
    internal const int AuthorityEncodedBytes = 48;

    internal static bool IsValid(in Protocol3BindingLimits value)
        => value.Slots is >= 1 and <= 65536 && value.Operations is >= 1 and <= 64 &&
           value.MaxClientStreamsPerCall is >= 1 and <= 127 &&
           value.OperationDeadlineMs is >= 1 and <= 60000 && value.QueuedControlFrames is >= 1 and <= 256;

    internal static bool IsWithin(in Protocol3BindingLimits value, in Protocol3BindingLimits ceiling)
        => IsValid(value) && IsValid(ceiling) && value.Slots <= ceiling.Slots && value.Operations <= ceiling.Operations &&
           value.MaxClientStreamsPerCall <= ceiling.MaxClientStreamsPerCall &&
           value.OperationDeadlineMs <= ceiling.OperationDeadlineMs && value.QueuedControlFrames <= ceiling.QueuedControlFrames;

    internal static Protocol3BindingLimits Negotiate(
        in Protocol3BindingLimits offer,
        in Protocol3BindingLimits local)
    {
        if (!IsValid(local))
            throw new ArgumentOutOfRangeException(nameof(local));
        if (!IsValid(offer))
            throw Protocol3BindingValidation.Malformed("Invalid binding limits offer.");
        return new Protocol3BindingLimits(
            Math.Min(offer.Slots, local.Slots), Math.Min(offer.Operations, local.Operations),
            Math.Min(offer.MaxClientStreamsPerCall, local.MaxClientStreamsPerCall),
            Math.Min(offer.OperationDeadlineMs, local.OperationDeadlineMs),
            Math.Min(offer.QueuedControlFrames, local.QueuedControlFrames));
    }

    internal static void ValidateNegotiated(
        in Protocol3BindingLimits chosen,
        in Protocol3BindingLimits offer,
        in Protocol3BindingLimits local)
    {
        if (!IsValid(offer) || !IsValid(local))
            throw new ArgumentException("Local binding limits must be valid.");
        if (!IsWithin(chosen, offer) || !IsWithin(chosen, local))
            throw Protocol3BindingValidation.Malformed("Negotiated binding limits exceed the offer or local policy.");
    }

    internal static void Write(Span<byte> destination, in Protocol3BindingLimits value)
    {
        if (destination.Length != EncodedBytes)
            throw new ArgumentException("Binding limits require exactly 16 bytes.", nameof(destination));
        if (!IsValid(value))
            throw new ArgumentOutOfRangeException(nameof(value));
        BinaryPrimitives.WriteUInt32LittleEndian(destination, value.Slots);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], value.Operations);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], value.MaxClientStreamsPerCall);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], value.OperationDeadlineMs);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], value.QueuedControlFrames);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], 0);
    }

    internal static Protocol3BindingLimits Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != EncodedBytes || BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]) != 0)
            throw Protocol3BindingValidation.Malformed("Invalid binding limits length or reserved field.");
        var value = new Protocol3BindingLimits(
            BinaryPrimitives.ReadUInt32LittleEndian(bytes), BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]), BinaryPrimitives.ReadUInt32LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]));
        if (!IsValid(value))
            throw Protocol3BindingValidation.Malformed("Binding limits are outside their fixed bounds.");
        return value;
    }

    internal static void WriteAuthority(Span<byte> destination, in Protocol3BindingAuthority authority)
    {
        if (destination.Length != AuthorityEncodedBytes || authority.ServerInstanceId == Guid.Empty ||
            authority.RegistryEpoch == Guid.Empty || !IsValid(authority.Limits))
            throw new ArgumentException("Invalid binding authority, limits, or destination.");
        // Protocol3 uses default .NET Guid byte layout, NOT RFC/network-order GUIDs.
        authority.ServerInstanceId.TryWriteBytes(destination[..16]);
        authority.RegistryEpoch.TryWriteBytes(destination[16..32]);
        Write(destination[32..], authority.Limits);
    }

    internal static Protocol3BindingAuthority ReadAuthority(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != AuthorityEncodedBytes)
            throw Protocol3BindingValidation.Malformed("Binding authority must be exactly 48 bytes.");
        var server = new Guid(bytes[..16]);
        var epoch = new Guid(bytes[16..32]);
        if (server == Guid.Empty || epoch == Guid.Empty)
            throw Protocol3BindingValidation.Malformed("Binding authority cannot be empty.");
        return new Protocol3BindingAuthority(server, epoch, Read(bytes[32..]));
    }
}
