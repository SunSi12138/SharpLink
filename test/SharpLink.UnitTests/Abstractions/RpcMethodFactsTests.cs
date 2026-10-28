using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.UnitTests.Abstractions;

public class RpcMethodFactsTests
{
    [Test]
    public async Task CompleteFactsShouldProjectEveryScalarWithoutLosingTimeoutPresence()
    {
        foreach (var timeout in new TimeSpan?[] { null, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue })
        {
            var facts = new RpcMethodFacts(31, 47, RpcMethodKind.DuplexStreaming, 127,
                supportsCancellation: true, hasResponsePayload: true, responseNullable: true,
                hasMethodTimeout: true, methodTimeout: timeout, isIdempotent: true);
            var descriptor = facts.ToDescriptor();
            await Assert.That(descriptor).IsEqualTo(new RpcMethodDescriptor(31, 47,
                RpcMethodKind.DuplexStreaming, true, true, true, timeout, true, 127, true));
            await Assert.That(facts.SupportsCancellation).IsTrue();
            await Assert.That(facts.MethodTimeout).IsEqualTo(timeout);
            await Assert.That(facts.ClientStreamCount).IsEqualTo(127);
        }
    }

    [Test]
    public async Task CachedDescriptorShouldPreserveEveryShapeAndReturnIndependentValueCopies()
    {
        foreach (var kind in Enum.GetValues<RpcMethodKind>())
        foreach (var declaredTimeout in new[] { false, true })
        foreach (var timeout in new TimeSpan?[] { null, TimeSpan.Zero, TimeSpan.FromTicks(-1), TimeSpan.MaxValue })
        {
            if (!declaredTimeout && timeout.HasValue)
                continue;
            var count = kind is RpcMethodKind.ClientStreaming or RpcMethodKind.DuplexStreaming ? 127 : 0;
            var payload = kind != RpcMethodKind.OneWay;
            var cancellation = kind is not (RpcMethodKind.Unary or RpcMethodKind.OneWay);
            var facts = new RpcMethodFacts(long.MinValue, long.MaxValue, kind, count,
                cancellation, payload, payload, declaredTimeout, timeout, true);
            var expected = new RpcMethodDescriptor(long.MinValue, long.MaxValue, kind,
                payload, count != 0, declaredTimeout, timeout, true, count, payload);
            await Assert.That(facts.IsKnown).IsTrue();
            await Assert.That(facts.ToDescriptor()).IsEqualTo(expected);
            await Assert.That(facts.ContractId).IsEqualTo(long.MinValue);
            await Assert.That(facts.MethodId).IsEqualTo(long.MaxValue);
            await Assert.That(facts.Kind).IsEqualTo(kind);
            await Assert.That(facts.ClientStreamCount).IsEqualTo(count);
            await Assert.That(facts.HasClientStreams).IsEqualTo(count != 0);
            await Assert.That(facts.SupportsCancellation).IsEqualTo(cancellation);
            await Assert.That(facts.HasResponsePayload).IsEqualTo(payload);
            await Assert.That(facts.ResponseNullable).IsEqualTo(payload);
            await Assert.That(facts.HasMethodTimeout).IsEqualTo(declaredTimeout);
            await Assert.That(facts.MethodTimeout).IsEqualTo(timeout);
            await Assert.That(facts.IsIdempotent).IsTrue();
            var changedCopy = facts.ToDescriptor() with { MethodId = 0, MethodTimeout = TimeSpan.FromTicks(7) };
            await Assert.That(changedCopy.MethodId).IsEqualTo(0);
            await Assert.That(facts.ToDescriptor()).IsEqualTo(expected);
        }
    }

    [Test]
    [Arguments(-1)]
    [Arguments(128)]
    public async Task StreamCountOutsideSupportedRangeShouldFailAtConstruction(int count)
        => await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.ClientStreaming,
            count, true, true)).Throws<ArgumentOutOfRangeException>();

    [Test]
    public async Task ContradictoryFactsShouldNeverDescribeAKnownMethod()
    {
        await Assert.That(() => new RpcMethodFacts(1, 2, (RpcMethodKind)99, 0, false, false))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.ClientStreaming, 0, true, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.Unary, 1, true, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.ServerStreaming, 0, false, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.OneWay, 0, false, true))
            .Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.Unary, 0, false, false,
            responseNullable: true)).Throws<ArgumentException>();
        await Assert.That(() => new RpcMethodFacts(1, 2, RpcMethodKind.Unary, 0, false, false,
            methodTimeout: TimeSpan.FromSeconds(1))).Throws<ArgumentException>();
        await Assert.That(RpcMethodFacts.UnknownMethod.IsKnown).IsFalse();
        await Assert.That(() => RpcMethodFacts.UnknownMethod.ToDescriptor()).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task RetainableFactsShouldContainOnlyReadonlyHostScalars()
    {
        var type = typeof(RpcMethodFacts);
        await Assert.That(type.IsSealed).IsTrue();
        await Assert.That(type.Assembly).IsEqualTo(typeof(IRpcStub).Assembly);
        await Assert.That(RuntimeHelpers.IsReferenceOrContainsReferences<RpcMethodDescriptor>()).IsFalse();
        await AssertReadonlyScalarFields(type);
    }

    private static async Task AssertReadonlyScalarFields(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            await Assert.That(field.IsInitOnly).IsTrue();
            await Assert.That(field.FieldType.IsValueType).IsTrue();
            if (!field.FieldType.IsPrimitive && !field.FieldType.IsEnum)
                await AssertReadonlyScalarFields(field.FieldType);
        }
    }
}
