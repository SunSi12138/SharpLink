using System.Buffers.Binary;

namespace SharpLink.UnitTests.Protocol;

public sealed class ImmutableRouteManifestTests
{
    [Test]
    public void PreparedTransportIdentityMustNotChangeLogicalDescriptorEquality()
    {
        var logical = new RpcMethodDescriptor(42, 43, RpcMethodKind.Unary, true, false, false, null);
        var prepared = logical with { WireRouteId = 9 };
        Ensure(logical == prepared && logical.GetHashCode() == prepared.GetHashCode(),
            "Internal wire routing must not change client policies keyed by logical method metadata.");
        Ensure(logical.Equals((object)prepared) && logical.ToString() == prepared.ToString(),
            "Object equality and public printed metadata must remain logical.");
        Ensure(default(RpcMethodDescriptor).WireRouteId == 0 &&
            (default(RpcMethodDescriptor) with { ContractId = 17 }).WireRouteId == 17,
            "Default/init construction must retain ordinary wire meaning.");
        Ensure(prepared.ContractId == 42 && prepared.WireRouteId == 9, "Transport identity must remain independent.");
        var changedLogical = prepared with { ContractId = 55 };
        Ensure(changedLogical.ContractId == 55 && changedLogical.WireRouteId == 55,
            "Legacy ContractId initializer must retain its ordinary wire meaning.");
    }

    [Test]
    public void ExperimentalManifestShouldRoundTripExactScopeAndContractRoutes()
    {
        var scope = Guid.NewGuid();
        using var writer = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(writer, new ProtocolV2ContractManifest(
            9, [new(42, new RpcHash128(1, 2)), new(3, new RpcHash128(3, 4))],
            scope, [new(42, 7), new(3, 8)]), new());
        var decoded = ProtocolV2ContractManifestCodec.Read(new(writer.WrittenMemory), new());
        Ensure(decoded.RouteScope == scope && decoded.Routes[42] == 7 && decoded.Routes[3] == 8,
            "Scope and exact logical-to-wire routes must survive contract sorting.");
        Ensure(decoded.Contracts[42] == new RpcHash128(1, 2), "Exact assembly hash must remain logical.");
    }

    [Test]
    public void LegacyManifestShouldRetainUnextendedWireShape()
    {
        using var writer = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(writer,
            new ProtocolV2ContractManifest(0, [new(42, new RpcHash128(1, 2))]), new());
        Ensure(writer.WrittenMemory.Length == 12 + 24, "Legacy manifest wire length changed.");
        var decoded = ProtocolV2ContractManifestCodec.Read(new(writer.WrittenMemory), new());
        Ensure(decoded.RouteScope == Guid.Empty && decoded.Routes.Count == 0, "Legacy manifest acquired a route scope.");
    }

    [Test]
    public void MalformedExperimentalRouteExtensionShouldFailClosed()
    {
        using var writer = new PooledByteBufferWriter();
        ProtocolV2ContractManifestCodec.Write(writer, new ProtocolV2ContractManifest(
            0, [new(1, new RpcHash128(1, 2)), new(2, new RpcHash128(3, 4))],
            Guid.NewGuid(), [new(1, 11), new(2, 12)]), new());
        var original = writer.WrittenMemory.ToArray();
        const int extensionOffset = 12 + 2 * 24;
        var badMarker = (byte[])original.Clone();
        badMarker[extensionOffset] ^= 0xFF;
        Reject(badMarker);
        var emptyScope = (byte[])original.Clone();
        emptyScope.AsSpan(extensionOffset + 4, 16).Clear();
        Reject(emptyScope);
        var duplicateRoute = (byte[])original.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(duplicateRoute.AsSpan(extensionOffset + 20 + 8), 11);
        Reject(duplicateRoute);
        var zeroRoute = (byte[])original.Clone();
        BinaryPrimitives.WriteInt64LittleEndian(zeroRoute.AsSpan(extensionOffset + 20), 0);
        Reject(zeroRoute);
        Reject(original[..^1]);
    }

    private static void Reject(byte[] bytes)
    {
        try { ProtocolV2ContractManifestCodec.Read(new ReadOnlySequence<byte>(bytes), new()); }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ProtocolViolation) { return; }
        throw new Exception("Malformed route manifest was accepted.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
