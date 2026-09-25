using System.Runtime.CompilerServices;

namespace SharpLink.UnitTests.Abstractions;

public class RpcMethodDescriptorTests
{
    [Test]
    public async Task FlagsMustRemainPackedAndBothDeconstructionShapesMustRemainCompatible()
    {
        var timeout = TimeSpan.FromSeconds(3);
        var descriptor = new RpcMethodDescriptor(
            11,
            22,
            RpcMethodKind.DuplexStreaming,
            HasResponsePayload: true,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: timeout,
            IsIdempotent: true,
            ClientStreamCount: 2,
            ResponseNullable: true);

        var (contractId, methodId, kind, hasResponse, hasStreams, hasTimeout,
            oldTimeout, idempotent, streamCount) = descriptor;
        descriptor.Deconstruct(
            out _, out _, out _, out _, out _, out _, out _, out _, out _, out var nullable);
        // RpcMethodDescriptor is no longer an init-only record, so the modified variant is built
        // through the constructor: both flags false while ClientStreamCount stays 2.
        var changed = new RpcMethodDescriptor(
            contractId,
            methodId,
            RpcMethodKind.DuplexStreaming,
            HasResponsePayload: true,
            HasClientStreams: false,
            HasMethodTimeout: true,
            MethodTimeout: timeout,
            IsIdempotent: true,
            ClientStreamCount: 2,
            ResponseNullable: false);

        await Assert.That(Unsafe.SizeOf<RpcMethodDescriptor>()).IsLessThanOrEqualTo(48);
        await Assert.That(contractId).IsEqualTo(11);
        await Assert.That(methodId).IsEqualTo(22);
        await Assert.That(kind).IsEqualTo(RpcMethodKind.DuplexStreaming);
        await Assert.That(hasResponse && hasStreams && hasTimeout && idempotent && nullable).IsTrue();
        await Assert.That(oldTimeout).IsEqualTo(timeout);
        await Assert.That(streamCount).IsEqualTo(2);
        await Assert.That(changed.ResponseNullable || changed.HasClientStreams).IsFalse();
        await Assert.That(changed.ClientStreamCount).IsEqualTo(2);
        await Assert.That(changed.HasResponsePayload && changed.HasMethodTimeout && changed.IsIdempotent).IsTrue();
    }
}
