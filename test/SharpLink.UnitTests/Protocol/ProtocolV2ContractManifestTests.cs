using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace SharpLink.UnitTests.Protocol;

public sealed class ProtocolV2ContractManifestTests
{
    private static readonly SharpLinkProtocolOptions Limits = new();

    [Test]
    public void ContractManifestShouldRoundTripInDeterministicContractOrder()
    {
        var firstHash = new RpcHash128(0x0102030405060708UL, 0x1112131415161718UL);
        var secondHash = new RpcHash128(0x2122232425262728UL, 0x3132333435363738UL);
        var manifest = new ProtocolV2ContractManifest(
            7,
            [
                new KeyValuePair<long, RpcHash128>(42, secondHash),
                new KeyValuePair<long, RpcHash128>(3, firstHash)
            ]);
        using var writer = new PooledByteBufferWriter();

        ProtocolV2ContractManifestCodec.Write(writer, manifest, Limits);
        var decoded = ProtocolV2ContractManifestCodec.Read(
            new ReadOnlySequence<byte>(writer.WrittenMemory),
            Limits);

        Ensure(decoded.Generation == 7, "manifest generation round-trip");
        Ensure(decoded.OrderedContracts.Count == 2, "manifest entry count round-trip");
        Ensure(decoded.OrderedContracts[0].Key == 3 && decoded.OrderedContracts[0].Value == firstHash,
            "manifest encoding must normalize entries by ContractId");
        Ensure(decoded.OrderedContracts[1].Key == 42 && decoded.OrderedContracts[1].Value == secondHash,
            "manifest encoding must preserve the exact RpcAssemblyHash for each ContractId");
    }

    [Test]
    public void ContractManifestShouldRejectInvalidIdentityEntries()
    {
        var validHash = new RpcHash128(1, 2);

        EnsureThrows<ArgumentException>(() => new ProtocolV2ContractManifest(
            0,
            [new KeyValuePair<long, RpcHash128>(0, validHash)]));
        EnsureThrows<ArgumentException>(() => new ProtocolV2ContractManifest(
            0,
            [new KeyValuePair<long, RpcHash128>(1, default)]));
        EnsureThrows<ArgumentException>(() => new ProtocolV2ContractManifest(
            0,
            [
                new KeyValuePair<long, RpcHash128>(1, validHash),
                new KeyValuePair<long, RpcHash128>(1, validHash)
            ]));
    }

    [Test]
    public void VersionOneManifestShouldRejectMalformedWireIdentitiesAndCounts()
    {
        const int headerBytes = 45;
        const int entryBytes = 40;
        var manifest = new ProtocolV2ContractManifest(Guid.NewGuid(), Guid.NewGuid(), 7,
            [new(3, new RpcHash128(1, 2)), new(42, new RpcHash128(3, 4))],
            [new(3, Guid.NewGuid()), new(42, Guid.NewGuid())]);
        using var writer = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(writer, manifest, Limits);
        var original = writer.WrittenMemory.ToArray();
        Ensure(original.Length == headerBytes + 2 * entryBytes, "v1 header and entry sizes");
        Ensure(original[0] == 1 && BinaryPrimitives.ReadInt32LittleEndian(original.AsSpan(41)) == 2, "v1 count offset");
        RejectMutation(bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(33), -1));
        RejectMutation(bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(41), -1));
        RejectMutation(bytes => BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(41), 3));
        RejectMutation(bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(headerBytes), 0));
        RejectMutation(bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(headerBytes + entryBytes), 3));
        RejectMutation(bytes => BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(headerBytes + entryBytes), 2));
        RejectMutation(bytes => bytes.AsSpan(headerBytes + 8, 16).Clear());
        RejectMutation(bytes => bytes.AsSpan(headerBytes + 24, 16).Clear());
        RejectMutation(bytes => bytes.AsSpan(1, 16).Clear());
        for (var length = 0; length < original.Length; length++)
        {
            var truncated = original[..length];
            EnsureThrows<SharpLinkException>(() => ProtocolV2ContractManifestCodec.Read(new(truncated), Limits));
        }
        void RejectMutation(Action<byte[]> mutate)
        {
            var bytes = (byte[])original.Clone();
            mutate(bytes);
            EnsureThrows<SharpLinkException>(() => ProtocolV2ContractManifestCodec.Read(new(bytes), Limits));
        }
    }

    private static void EnsureThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException)
        {
            return;
        }

        throw new Exception($"Expected {typeof(TException).Name}.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
