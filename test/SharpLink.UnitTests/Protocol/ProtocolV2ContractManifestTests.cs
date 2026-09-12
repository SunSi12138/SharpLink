using System.Buffers;
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
