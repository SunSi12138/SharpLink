using System.Runtime.CompilerServices;

using SharpLink.Abstractions;

namespace SharpLink.UnitTests.Abstractions;

public sealed class RpcMethodShapeTests
{
    [Test]
    public async Task KnownShapeShouldRoundTripEveryPackedFact()
    {
        var shape = new RpcMethodShape(
            RpcMethodKind.DuplexStreaming,
            clientStreamCount: 3,
            supportsCancellation: true,
            hasResponsePayload: true,
            responseNullable: true,
            hasMethodTimeout: true,
            isIdempotent: true,
            hasMethodTimeoutValue: true,
            timeoutOrdinal: 7);

        await Assert.That(shape.IsKnown).IsTrue();
        await Assert.That(shape.IsUnresolvable).IsFalse();
        await Assert.That(shape.IsUnknownMethod).IsFalse();
        await Assert.That(shape.Kind).IsEqualTo(RpcMethodKind.DuplexStreaming);
        await Assert.That(shape.ClientStreamCount).IsEqualTo(3);
        await Assert.That(shape.HasKnownClientStreamCount).IsTrue();
        await Assert.That(shape.HasClientStreams).IsTrue();
        await Assert.That(shape.SupportsCancellation).IsTrue();
        await Assert.That(shape.HasResponsePayload).IsTrue();
        await Assert.That(shape.ResponseNullable).IsTrue();
        await Assert.That(shape.HasMethodTimeout).IsTrue();
        await Assert.That(shape.HasMethodTimeoutValue).IsTrue();
        await Assert.That(shape.IsIdempotent).IsTrue();
        await Assert.That(shape.TimeoutOrdinal).IsEqualTo(7);
    }

    [Test]
    public async Task KnownShapeShouldClearEveryUnsetPackedFact()
    {
        var shape = new RpcMethodShape(
            RpcMethodKind.Unary,
            clientStreamCount: 0,
            supportsCancellation: false,
            hasResponsePayload: false);

        await Assert.That(shape.IsKnown).IsTrue();
        await Assert.That(shape.Kind).IsEqualTo(RpcMethodKind.Unary);
        await Assert.That(shape.ClientStreamCount).IsEqualTo(0);
        await Assert.That(shape.HasClientStreams).IsFalse();
        await Assert.That(shape.SupportsCancellation).IsFalse();
        await Assert.That(shape.HasResponsePayload).IsFalse();
        await Assert.That(shape.ResponseNullable).IsFalse();
        await Assert.That(shape.HasMethodTimeout).IsFalse();
        await Assert.That(shape.HasMethodTimeoutValue).IsFalse();
        await Assert.That(shape.IsIdempotent).IsFalse();
        await Assert.That(shape.TimeoutOrdinal).IsEqualTo(0);
    }

    [Test]
    public async Task DefaultShapeShouldStayConservative()
    {
        var shape = default(RpcMethodShape);

        await Assert.That(shape.IsKnown).IsFalse();
        await Assert.That(shape.IsUnresolvable).IsTrue();
        await Assert.That(shape.IsUnknownMethod).IsFalse();
        // A stub that cannot describe its methods must keep creating per-call cancellation state.
        await Assert.That(shape.SupportsCancellation).IsTrue();
        await Assert.That(shape.HasKnownClientStreamCount).IsFalse();
        await Assert.That(shape.ClientStreamCount).IsEqualTo(RpcMethodShape.UnknownClientStreamCount);
        await Assert.That(shape.Kind).IsEqualTo(RpcMethodKind.Unary);
        await Assert.That(RpcMethodShape.Unresolvable).IsEqualTo(shape);
    }

    [Test]
    public async Task UnknownMethodShapeShouldDescribeAnAbsentNonCancellableMethod()
    {
        var shape = RpcMethodShape.UnknownMethod;

        await Assert.That(shape.IsKnown).IsFalse();
        await Assert.That(shape.IsUnknownMethod).IsTrue();
        await Assert.That(shape.IsUnresolvable).IsFalse();
        await Assert.That(shape.SupportsCancellation).IsFalse();
        await Assert.That(shape.HasKnownClientStreamCount).IsTrue();
        await Assert.That(shape.ClientStreamCount).IsEqualTo(0);
        await Assert.That(shape.Kind).IsEqualTo(RpcMethodKind.Unary);
    }

    [Test]
    public async Task ShapeShouldStayFourBytesAndValueComparable()
    {
        await Assert.That(Unsafe.SizeOf<RpcMethodShape>()).IsEqualTo(sizeof(uint));

        var left = new RpcMethodShape(RpcMethodKind.ClientStreaming, 2, true, true);
        var right = new RpcMethodShape(RpcMethodKind.ClientStreaming, 2, true, true);
        var other = new RpcMethodShape(RpcMethodKind.ClientStreaming, 1, true, true);

        await Assert.That(left).IsEqualTo(right);
        await Assert.That(left == right).IsTrue();
        await Assert.That(left != other).IsTrue();
        await Assert.That(left.Packed).IsEqualTo(right.Packed);
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    [Test]
    public async Task PackedConstructorShouldMatchTheFactConstructor()
    {
        var fromFacts = new RpcMethodShape(
            RpcMethodKind.ServerStreaming,
            clientStreamCount: 0,
            supportsCancellation: true,
            hasResponsePayload: true,
            responseNullable: true,
            hasMethodTimeout: true,
            isIdempotent: false,
            hasMethodTimeoutValue: true,
            timeoutOrdinal: 3);

        var fromPacked = new RpcMethodShape(fromFacts.Packed);

        await Assert.That(fromPacked).IsEqualTo(fromFacts);
        await Assert.That(fromPacked.Kind).IsEqualTo(RpcMethodKind.ServerStreaming);
        await Assert.That(fromPacked.TimeoutOrdinal).IsEqualTo(3);
        await Assert.That(fromPacked.HasMethodTimeoutValue).IsTrue();
    }

    [Test]
    public async Task ShapeShouldRejectImpossibleClientStreamCounts()
    {
        await Assert.That(() => new RpcMethodShape(
                RpcMethodKind.ClientStreaming,
                clientStreamCount: RpcMethodShape.UnknownClientStreamCount + 1,
                supportsCancellation: true,
                hasResponsePayload: true))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task FromShapeShouldProjectEveryDescriptorFact()
    {
        var shape = new RpcMethodShape(
            RpcMethodKind.ClientStreaming,
            clientStreamCount: 2,
            supportsCancellation: true,
            hasResponsePayload: true,
            responseNullable: true,
            hasMethodTimeout: true,
            isIdempotent: true,
            hasMethodTimeoutValue: true,
            timeoutOrdinal: 0);
        var timeout = TimeSpan.FromSeconds(5);

        var descriptor = RpcMethodDescriptor.FromShape(11, 22, shape, timeout);

        await Assert.That(descriptor.ContractId).IsEqualTo(11);
        await Assert.That(descriptor.MethodId).IsEqualTo(22);
        await Assert.That(descriptor.Kind).IsEqualTo(RpcMethodKind.ClientStreaming);
        await Assert.That(descriptor.ClientStreamCount).IsEqualTo(2);
        await Assert.That(descriptor.HasClientStreams).IsTrue();
        await Assert.That(descriptor.HasResponsePayload).IsTrue();
        await Assert.That(descriptor.ResponseNullable).IsTrue();
        await Assert.That(descriptor.HasMethodTimeout).IsTrue();
        await Assert.That(descriptor.IsIdempotent).IsTrue();
        await Assert.That(descriptor.MethodTimeout).IsEqualTo(timeout);
    }

    [Test]
    public async Task FromShapeShouldNotLeakTheUnknownStreamSentinel()
    {
        var unresolvable = RpcMethodDescriptor.FromShape(11, 22, RpcMethodShape.Unresolvable);
        await Assert.That(unresolvable.ClientStreamCount).IsEqualTo(0);
        await Assert.That(unresolvable.HasClientStreams).IsFalse();
        await Assert.That(unresolvable.Kind).IsEqualTo(RpcMethodKind.Unary);

        var unknownMethod = RpcMethodDescriptor.FromShape(11, 22, RpcMethodShape.UnknownMethod);
        await Assert.That(unknownMethod.ClientStreamCount).IsEqualTo(0);
        await Assert.That(unknownMethod.HasClientStreams).IsFalse();
        await Assert.That(unknownMethod.Kind).IsEqualTo(RpcMethodKind.Unary);
    }

    [Test]
    public async Task DescriptorShouldStaySmallerThanItsPublishedCeiling()
    {
        await Assert.That(Unsafe.SizeOf<RpcMethodDescriptor>()).IsLessThanOrEqualTo(48);
    }
}
