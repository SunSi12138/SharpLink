using System.Threading;

namespace SharpLink.UnitTests.Abstractions;

public class GeneratedRegistryTests
{
    [Test]
    public void ProxyRegistrationShouldBeIdempotentAndRejectConflicts()
    {
        Func<IRpcChannel, object> factory = static channel => new ProxyMarker(channel);
        GeneratedProxyRegistry.Register(typeof(IRegistryContract), factory);
        GeneratedProxyRegistry.Register(typeof(IRegistryContract), factory);

        AssertThrows<InvalidOperationException>(() =>
            GeneratedProxyRegistry.Register(typeof(IRegistryContract), static channel => new ConflictingProxyMarker(channel)));
    }

    [Test]
    public void StubRegistrationShouldBeIdempotentAndRejectConflicts()
    {
        Func<IRpcStub> factory = static () => new StubMarker();
        GeneratedStubRegistry.Register(typeof(RegistryService), factory);
        GeneratedStubRegistry.Register(typeof(RegistryService), factory);

        AssertThrows<InvalidOperationException>(() =>
            GeneratedStubRegistry.Register(typeof(RegistryService), static () => new ConflictingStubMarker()));
    }

    private static void AssertThrows<TException>(Action action) where TException : Exception
    {
        try
        {
            action();
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private interface IRegistryContract
    {
    }

    private sealed class RegistryService
    {
    }
    private sealed record ProxyMarker(IRpcChannel Channel);
    private sealed record ConflictingProxyMarker(IRpcChannel Channel);

    private class StubMarker : IRpcStub
    {
        public long InterfaceHash => 1;
        public ValueTask InvokeNoReturnAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args)
            => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, ArrayBufferWriter<byte> output)
            => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, ArrayBufferWriter<byte> output, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class ConflictingStubMarker : StubMarker
    {
    }
}
