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

    [Test]
    public void GeneratedCodecRegistrationShouldBeIdempotentRejectConflictsAndFreezePerContext()
    {
        var beforeRegistration = new SharpLinkRuntimeContextBuilder().Build();
        var factory = new RegistryCodecFactory("registry-schema-v1");
        RpcGeneratedCodecRegistry.Register(factory);
        RpcGeneratedCodecRegistry.Register(new RegistryCodecFactory("registry-schema-v1"));

        AssertThrows<NotSupportedException>(() => beforeRegistration.Codecs.GetCodec<RegistryDto>());
        var afterRegistration = new SharpLinkRuntimeContextBuilder().Build();
        Ensure(afterRegistration.Codecs.GetCodec<RegistryDto>() is RegistryDtoCodec, "generated context codec");
        AssertThrows<InvalidOperationException>(() =>
            RpcGeneratedCodecRegistry.Register(new RegistryCodecFactory("registry-schema-v2")));
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
        public ValueTask InvokeAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output)
            => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcSession session, long methodHash, long requestId, ReadOnlySequence<byte> args, IRpcByteBufferWriter output, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }

    private sealed class ConflictingStubMarker : StubMarker
    {
    }

    private sealed class RegistryDto;

    private sealed class RegistryDtoCodec : IRpcCodec<RegistryDto>
    {
        public void Serialize(in RegistryDto value, IBufferWriter<byte> buffer)
        {
        }

        public RegistryDto Deserialize(in ReadOnlySequence<byte> buffer) => new();
    }

    private sealed class RegistryCodecFactory(string schemaId) : IRpcGeneratedCodecFactory
    {
        public Type TargetType => typeof(RegistryDto);
        public string SchemaId { get; } = schemaId;
        public IRpcCodec Create(IRpcCodecProvider provider) => new RegistryDtoCodec();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
