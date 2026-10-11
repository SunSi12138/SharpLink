using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

public sealed class ProtocolV2ContractRegistrationTests
{
    [Test]
    [Arguments(ContractRegistrationMatch.Compatible)]
    [Arguments(ContractRegistrationMatch.MinimumVersion)]
    [Arguments(ContractRegistrationMatch.ExactRegistration)]
    public void RequestConstraintShouldRoundTripAndRejectEveryTruncation(ContractRegistrationMatch match)
    {
        var receipt = new ContractRegistrationReceipt(Guid.NewGuid(), Guid.NewGuid(), 42, 17, new(5, 6), Guid.NewGuid());
        var requirement = new ContractRequirement(receipt, match);
        var bytes = new byte[ProtocolV2ContractAdmissionCodec.GetPayloadLength(requirement)];
        ProtocolV2ContractAdmissionCodec.Write(bytes, requirement);
        var reader = new SequenceReader<byte>(new(bytes));
        var hash = ProtocolV2ContractAdmissionCodec.Read(ref reader, out var constraint);
        Ensure(hash == receipt.RpcAssemblyHash && constraint.Match == match && reader.Remaining == 0, "constraint extent and hash");
        if (match != ContractRegistrationMatch.Compatible)
            Ensure(constraint.ServerInstanceId == receipt.ServerInstanceId && constraint.RegistryEpoch == receipt.RegistryEpoch &&
                constraint.Generation == receipt.Generation && constraint.RegistrationId == receipt.RegistrationId, "scalar receipt roundtrip");
        for (var length = 0; length < bytes.Length; length++)
        {
            var truncated = bytes[..length];
            Reject(() => Read(truncated));
        }
        bytes[16] = 0;
        Reject(() => Read(bytes));
        bytes[16] = 1;
        bytes[17] = 255;
        Reject(() => Read(bytes));
    }

    [Test]
    public void ManifestShouldRoundTripIndependentServerEpochAndRegistrationIdentity()
    {
        var server = Guid.NewGuid();
        var epoch = Guid.NewGuid();
        var registration = Guid.NewGuid();
        var original = new ProtocolV2ContractManifest(server, epoch, 7,
            [new(12, new RpcHash128(1, 2))], [new(12, registration)]);
        using var writer = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(writer, original, new());
        var manifest = ProtocolV2ContractManifestCodec.Read(new(writer.WrittenMemory), new());
        Ensure(manifest.ServerInstanceId == server && manifest.RegistryEpoch == epoch && manifest.Generation == 7, "manifest timeline");
        Ensure(manifest.Contracts[12] == new RpcHash128(1, 2) && manifest.RegistrationIds[12] == registration, "registration separate from hash");
        var bytes = writer.WrittenMemory.ToArray();
        bytes[0] = 0;
        Reject(() => ProtocolV2ContractManifestCodec.Read(new(bytes), new()));
        bytes[0] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(41), int.MaxValue);
        Reject(() => ProtocolV2ContractManifestCodec.Read(new(bytes), new()));
    }

    [Test]
    public void CompatibilityOnlyFixtureCannotSatisfyVersionedRequirement()
    {
        var receipt = new ContractRegistrationReceipt(Guid.NewGuid(), Guid.NewGuid(), 0, 12, new(1, 2), Guid.NewGuid());
        var requirement = new ContractRequirement(receipt, ContractRegistrationMatch.MinimumVersion);
        var fixture = new ProtocolV2ContractManifest(100, [new(12, new RpcHash128(1, 2))]);
        Ensure(!requirement.Matches(fixture.ServerInstanceId, fixture.RegistryEpoch, fixture.Generation, Guid.Empty), "scoped generation comparison");
    }

    private static void Read(byte[] bytes)
    {
        var reader = new SequenceReader<byte>(new(bytes));
        ProtocolV2ContractAdmissionCodec.Read(ref reader, out _);
    }
    private static void Reject(Action action)
    {
        try { action(); }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation) { return; }
        throw new Exception("Expected bounded protocol rejection.");
    }
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
