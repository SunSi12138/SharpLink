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
            hasMethodTimeoutValue: true);

        await Assert.That(shape.IsKnown).IsTrue();
        await Assert.That(shape.IsInvalid).IsFalse();
        await Assert.That(shape.IsUnknownMethod).IsFalse();
        await Assert.That(shape.Kind).IsEqualTo(RpcMethodKind.DuplexStreaming);
        await Assert.That(shape.ClientStreamCount).IsEqualTo(3);
        await Assert.That(shape.HasClientStreams).IsTrue();
        await Assert.That(shape.SupportsCancellation).IsTrue();
        await Assert.That(shape.HasResponsePayload).IsTrue();
        await Assert.That(shape.ResponseNullable).IsTrue();
        await Assert.That(shape.HasMethodTimeout).IsTrue();
        await Assert.That(shape.HasMethodTimeoutValue).IsTrue();
        await Assert.That(shape.IsIdempotent).IsTrue();
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
    }

    [Test]
    public async Task DefaultShapeShouldBeInvalidAndDistinctFromUnknownMethod()
    {
        var shape = default(RpcMethodShape);
        await Assert.That(shape.IsKnown).IsFalse();
        await Assert.That(shape.IsInvalid).IsTrue();
        await Assert.That(shape.IsUnknownMethod).IsFalse();
        await Assert.That(shape == RpcMethodShape.UnknownMethod).IsFalse();
        await Assert.That(shape.SupportsCancellation).IsFalse();
    }

    [Test]
    public async Task UnknownMethodShapeShouldDescribeAnAbsentNonCancellableMethod()
    {
        var shape = RpcMethodShape.UnknownMethod;

        await Assert.That(shape.IsKnown).IsFalse();
        await Assert.That(shape.IsUnknownMethod).IsTrue();
        await Assert.That(shape.IsInvalid).IsFalse();
        await Assert.That(shape.SupportsCancellation).IsFalse();
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
        await Assert.That(left.GetHashCode()).IsEqualTo(right.GetHashCode());
    }

    [Test]
    public async Task TypedShapeCopiesShouldPreserveAllFacts()
    {
        var fromFacts = new RpcMethodShape(
            RpcMethodKind.ServerStreaming,
            clientStreamCount: 0,
            supportsCancellation: true,
            hasResponsePayload: true,
            responseNullable: true,
            hasMethodTimeout: true,
            isIdempotent: false,
            hasMethodTimeoutValue: true);

        var fromPacked = fromFacts;

        await Assert.That(fromPacked).IsEqualTo(fromFacts);
        await Assert.That(fromPacked.Kind).IsEqualTo(RpcMethodKind.ServerStreaming);
        await Assert.That(fromPacked.HasMethodTimeoutValue).IsTrue();
    }

    [Test]
    public async Task ShapeShouldRejectImpossibleClientStreamCounts()
    {
        await Assert.That(() => new RpcMethodShape(
                RpcMethodKind.ClientStreaming,
                clientStreamCount: RpcMethodShape.MaxClientStreamCount + 1,
                supportsCancellation: true,
                hasResponsePayload: true))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(15)]
    [Arguments(16)]
    [Arguments(127)]
    public async Task StreamCountBoundariesShouldRoundTripWithoutCorruptingFlags(int count)
    {
        var shape = new RpcMethodShape(count == 0 ? RpcMethodKind.ServerStreaming : RpcMethodKind.DuplexStreaming, count, true, true,
            responseNullable: true, hasMethodTimeout: true, isIdempotent: true,
            hasMethodTimeoutValue: true);
        var packed = shape;
        await Assert.That(packed.ClientStreamCount).IsEqualTo(count);
        await Assert.That(packed.Kind).IsEqualTo(count == 0 ? RpcMethodKind.ServerStreaming : RpcMethodKind.DuplexStreaming);
        await Assert.That(packed.SupportsCancellation).IsTrue();
        await Assert.That(packed.HasResponsePayload).IsTrue();
        await Assert.That(packed.ResponseNullable).IsTrue();
        await Assert.That(packed.HasMethodTimeout).IsTrue();
        await Assert.That(packed.HasMethodTimeoutValue).IsTrue();
        await Assert.That(packed.IsIdempotent).IsTrue();
    }

    [Test]
    [Arguments(-1)]
    [Arguments(128)]
    [Arguments(int.MinValue)]
    [Arguments(int.MaxValue)]
    public async Task OutOfRangeStreamCountsShouldThrow(int count)
    {
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.ClientStreaming, count, true, true))
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
            hasMethodTimeoutValue: true);
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
    public async Task DescriptorProjectionShouldRejectInvalidAndUnknownShapes()
    {
        await Assert.That(() => RpcMethodDescriptor.FromShape(11, 22, default))
            .Throws<ArgumentException>();
        await Assert.That(() => RpcMethodDescriptor.FromShape(11, 22, RpcMethodShape.UnknownMethod))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task PublicShapeSurfaceShouldNotExposePackedRepresentationOrDefaultStubImplementation()
    {
        await Assert.That(typeof(RpcMethodShape).GetConstructor([typeof(uint)])).IsNull();
        await Assert.That(typeof(RpcMethodShape).GetProperty("Packed")).IsNull();
        await Assert.That(typeof(IRpcStub).GetMethod(nameof(IRpcStub.ResolveMethodShape))!.IsAbstract).IsTrue();
    }

    [Test]
    [Arguments(5)]
    [Arguments(7)]
    [Arguments(255)]
    public async Task UndefinedMethodKindsShouldThrow(int kind)
    {
        await Assert.That(() => new RpcMethodShape((RpcMethodKind)kind, 0, true, true))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ContradictoryFlagsShouldThrow()
    {
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.Unary, 1, true, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.ClientStreaming, 0, true, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.ClientStreaming, 1, false, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.ServerStreaming, 0, false, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.ServerStreaming, 0, true, false))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.OneWay, 0, true, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.Unary, 0, true, false, responseNullable: true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodShape(RpcMethodKind.Unary, 0, true, true, hasMethodTimeoutValue: true))
            .Throws<ArgumentException>();
    }

    [Test]
    public async Task OneWayClientStreamsAndTimeoutWithoutExplicitValueShouldRemainSupported()
    {
        var shape = new RpcMethodShape(RpcMethodKind.OneWay, 127, true, false, hasMethodTimeout: true);
        await Assert.That(shape.IsKnown).IsTrue();
        await Assert.That(shape.ClientStreamCount).IsEqualTo(127);
        await Assert.That(shape.HasMethodTimeout).IsTrue();
        await Assert.That(shape.HasMethodTimeoutValue).IsFalse();
    }

    [Test]
    public async Task DescriptorShouldStaySmallerThanItsPublishedCeiling()
    {
        await Assert.That(Unsafe.SizeOf<RpcMethodDescriptor>()).IsLessThanOrEqualTo(48);
    }
}
