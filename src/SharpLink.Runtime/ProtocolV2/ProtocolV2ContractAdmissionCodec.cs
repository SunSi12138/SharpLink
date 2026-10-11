namespace SharpLink.Runtime;

internal readonly record struct ProtocolV2ContractConstraint(
    ContractRegistrationMatch Match,
    Guid ServerInstanceId,
    Guid RegistryEpoch,
    long Generation,
    Guid RegistrationId);

internal static class ProtocolV2ContractAdmissionCodec
{
    internal const byte Version = 1;

    internal static int EncodedLength(ContractRegistrationMatch match)
        => ProtocolV2Constants.ContractAdmissionPrefixBytes +
           (match == ContractRegistrationMatch.Compatible ? 0 : ProtocolV2Constants.ContractRegistrationConstraintBytes);

    internal static int GetPayloadLength(ContractRequirement requirement)
        => EncodedLength(requirement.Match);

    internal static void Write(Span<byte> destination, ContractRequirement requirement)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(destination, requirement.RpcAssemblyHash.High);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], requirement.RpcAssemblyHash.Low);
        destination[16] = Version;
        destination[17] = (byte)requirement.Match;
        if (requirement.Match == ContractRegistrationMatch.Compatible || requirement.Registration is not { } receipt)
            return;
        receipt.ServerInstanceId.TryWriteBytes(destination[18..34]);
        receipt.RegistryEpoch.TryWriteBytes(destination[34..50]);
        BinaryPrimitives.WriteInt64LittleEndian(destination[50..58], receipt.Generation);
        receipt.RegistrationId.TryWriteBytes(destination[58..74]);
    }

    internal static void Skip(ref SequenceReader<byte> reader)
    {
        if (reader.Remaining < ProtocolV2Constants.ContractIdentityBytes)
            throw ProtocolV2FrameParser.Violation("Request contract identity is truncated.");
        reader.Advance(ProtocolV2Constants.ContractIdentityBytes);
        var match = ReadVersionAndMatch(ref reader);
        if (match == ContractRegistrationMatch.Compatible)
            return;
        if (reader.Remaining < ProtocolV2Constants.ContractRegistrationConstraintBytes)
            throw ProtocolV2FrameParser.Violation("Request registration constraint is truncated.");
        reader.Advance(ProtocolV2Constants.ContractRegistrationConstraintBytes);
    }

    internal static RpcHash128 Read(ref SequenceReader<byte> reader, out ProtocolV2ContractConstraint constraint)
    {
        if (!reader.TryReadLittleEndian(out long high) || !reader.TryReadLittleEndian(out long low))
            throw ProtocolV2FrameParser.Violation("Request contract identity is truncated.");
        var hash = new RpcHash128(unchecked((ulong)high), unchecked((ulong)low));
        if (hash.IsEmpty)
            throw ProtocolV2FrameParser.Violation("Request contract identity cannot be empty.");
        var match = ReadVersionAndMatch(ref reader);
        constraint = default;
        if (match == ContractRegistrationMatch.Compatible)
            return hash;
        var server = ReadGuid(ref reader);
        var epoch = ReadGuid(ref reader);
        if (!reader.TryReadLittleEndian(out long generation) || generation < 0)
            throw ProtocolV2FrameParser.Violation("Request registration generation is invalid.");
        var registration = ReadGuid(ref reader);
        if (server == Guid.Empty || epoch == Guid.Empty ||
            match == ContractRegistrationMatch.ExactRegistration && registration == Guid.Empty)
            throw ProtocolV2FrameParser.Violation("Request registration constraint identities cannot be empty.");
        constraint = new(match, server, epoch, generation, registration);
        return hash;
    }

    private static ContractRegistrationMatch ReadVersionAndMatch(ref SequenceReader<byte> reader)
    {
        if (!reader.TryRead(out var version) || version != Version ||
            !reader.TryRead(out var match) || match > (byte)ContractRegistrationMatch.ExactRegistration)
            throw ProtocolV2FrameParser.Violation("Request contract admission version or match mode is unsupported.");
        return (ContractRegistrationMatch)match;
    }

    internal static Guid ReadGuid(ref SequenceReader<byte> reader)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!reader.TryCopyTo(bytes))
            throw ProtocolV2FrameParser.Violation("Contract registration identity is truncated.");
        reader.Advance(bytes.Length);
        return new Guid(bytes);
    }
}
