using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        var changed = descriptor with { ResponseNullable = false, HasClientStreams = false };

        await Assert.That(Unsafe.SizeOf<RpcMethodDescriptor>()).IsEqualTo(32);
        await Assert.That(Marshal.SizeOf<RpcMethodDescriptor>()).IsEqualTo(32);
        await Assert.That(contractId).IsEqualTo(11);
        await Assert.That(methodId).IsEqualTo(22);
        await Assert.That(kind).IsEqualTo(RpcMethodKind.DuplexStreaming);
        await Assert.That(hasResponse && hasStreams && hasTimeout && idempotent && nullable).IsTrue();
        await Assert.That(oldTimeout).IsEqualTo(timeout);
        await Assert.That(streamCount).IsEqualTo(2);
        await Assert.That(changed.ResponseNullable || changed.HasClientStreams).IsFalse();
        await Assert.That(changed.HasResponsePayload && changed.HasMethodTimeout && changed.IsIdempotent).IsTrue();
    }

    [Test]
    public async Task DefaultDescriptorMustHaveNoMethodTimeout()
    {
        var descriptor = default(RpcMethodDescriptor);

        await Assert.That(descriptor.MethodTimeout.HasValue).IsFalse();
        await Assert.That(descriptor with { MethodTimeout = null }).IsEqualTo(descriptor);
        await Assert.That((descriptor with { MethodTimeout = TimeSpan.Zero }).MethodTimeout)
            .IsEqualTo(TimeSpan.Zero);
        await Assert.That(descriptor != (descriptor with { MethodTimeout = TimeSpan.Zero })).IsTrue();
    }

    [Test]
    public async Task MethodTimeoutMustRoundTripAllNullableValuesAndPreserveRecordEquality()
    {
        static RpcMethodDescriptor Create(TimeSpan? timeout) => new(
            11, 22, RpcMethodKind.DuplexStreaming,
            HasResponsePayload: true,
            HasClientStreams: true,
            HasMethodTimeout: true,
            MethodTimeout: timeout,
            IsIdempotent: true,
            ClientStreamCount: 2,
            ResponseNullable: true);

        var withoutValue = Create(null);
        TimeSpan?[] timeouts =
        [
            null,
            TimeSpan.Zero,
            TimeSpan.FromTicks(-1),
            TimeSpan.FromTicks(-42),
            TimeSpan.FromSeconds(3),
            TimeSpan.MinValue,
            TimeSpan.MaxValue
        ];

        foreach (var timeout in timeouts)
        {
            var constructed = Create(timeout);
            var copied = withoutValue with { MethodTimeout = timeout };

            await Assert.That(constructed.MethodTimeout).IsEqualTo(timeout);
            await Assert.That(copied.MethodTimeout).IsEqualTo(timeout);
            await Assert.That(constructed).IsEqualTo(copied);
            await Assert.That(constructed.GetHashCode()).IsEqualTo(copied.GetHashCode());
            await Assert.That(copied.HasMethodTimeout).IsTrue();
            await Assert.That(copied.HasResponsePayload && copied.HasClientStreams
                && copied.IsIdempotent && copied.ResponseNullable).IsTrue();
        }

        var zero = Create(TimeSpan.Zero);
        var negative = Create(TimeSpan.FromTicks(-1));
        var positive = Create(TimeSpan.FromSeconds(3));

        await Assert.That(withoutValue != zero).IsTrue();
        await Assert.That(withoutValue != negative).IsTrue();
        await Assert.That(zero != negative).IsTrue();
        await Assert.That(negative != positive).IsTrue();
        await Assert.That(negative with { MethodTimeout = null }).IsEqualTo(withoutValue);
    }
}
