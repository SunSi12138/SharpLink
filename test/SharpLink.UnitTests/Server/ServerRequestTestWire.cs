using System.Buffers.Binary;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

internal static class ServerRequestTestWire
{
    internal static RpcHash128 Hash { get; } = new(0x7365727665722d77UL, 0x6972652d76352d31UL);

    internal static void WriteIdentity(IBufferWriter<byte> writer, long contractId, long methodId,
        RpcMethodKind kind = RpcMethodKind.Unary, ushort streamCount = 0)
        => ProtocolV2RequestIdentityCodec.Write(writer,
            new ProtocolV2RequestIdentity(contractId, methodId, Hash, kind, streamCount));

    internal static byte[] Create(IRpcStub stub, long methodId, TimeSpan? timeBudget = null)
    {
        var writer = new ArrayBufferWriter<byte>();
        var hasDescriptor = stub.TryGetMethodDescriptor(methodId, out var descriptor);
        WriteIdentity(writer, stub.InterfaceHash, methodId,
            hasDescriptor ? descriptor.Kind : RpcMethodKind.Unary,
            hasDescriptor ? checked((ushort)descriptor.ClientStreamCount) : (ushort)0);
        if (timeBudget is { } budget)
        {
            BinaryPrimitives.WriteInt64LittleEndian(writer.GetSpan(sizeof(long)), budget.Ticks);
            writer.Advance(sizeof(long));
        }
        return writer.WrittenSpan.ToArray();
    }

    internal static void Bind(ServiceRegistration registration, ServerServiceModuleRegistry registry)
        => registration.PublishReceipt(new ContractRegistrationReceipt(
            registry.ServerInstanceId, registry.RegistryEpoch, 0,
            registration.Stub.InterfaceHash, Hash, Guid.NewGuid()));
}
