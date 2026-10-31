using System.Reflection;

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
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        await Assert.That(fields.Length).IsEqualTo(4);
        foreach (var field in fields)
        {
            await Assert.That(field.IsInitOnly).IsTrue();
            await Assert.That(field.FieldType == typeof(long) || field.FieldType == typeof(uint)).IsTrue();
        }
    }
}
