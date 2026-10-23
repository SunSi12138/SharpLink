using System.IO.Pipelines;
using System.Reflection;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class AbandonedGeneratedMapperOwnershipTests
{
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(false, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    [Arguments(true, false, false)]
    [Arguments(true, true, false)]
    [Arguments(true, false, true)]
    public async Task UnassociatedApplicationTerminalsPreserveDisposalAndMappingPrecedence(
        bool canceled, bool applicationWrapper, bool disposalFault)
    {
        Exception original = canceled
            ? applicationWrapper ? new TaskCanceledException("application task cancellation") : new OperationCanceledException("application cancellation")
            : applicationWrapper ? new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, 992, "application wrapper", new ApplicationDeadline()) : new ApplicationDeadline();
        var replacement = new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, 884, "disposal replacement", original);
        var expected = disposalFault ? replacement : original;
        await using var fixture = new Fixture(expected);
        var stream = new FailureStream(original, boundary: 0);
        var pump = fixture.Pump(stream);
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(Watchdog);
            Ensure(!pump.IsCompleted && !fixture.Mapper.Observed.Task.IsCompleted,
                "an unassociated application terminal must preserve ordinary disposal-before-mapping semantics");
            if (disposalFault) stream.DisposeRelease.TrySetException(replacement);
            else stream.DisposeRelease.TrySetResult();
            Ensure(ReferenceEquals(await fixture.Mapper.Observed.Task.WaitAsync(Watchdog), expected),
                "mapping preserves the original subtype/identity and disposal failure precedence");
            await pump.WaitAsync(Watchdog);
            await fixture.AssertMappedWireAsync();
        }
        finally { stream.DisposeRelease.TrySetResult(); await pump.WaitAsync(Watchdog); }
    }

    private sealed class ApplicationDeadline()
        : SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, 947, "custom application deadline");

    private sealed class IdentityMapper(Exception expected) : IRpcExceptionMapper
    {
        internal TaskCompletionSource<Exception> Observed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ExpectedMessage => expected is SharpLinkException failure ? $"mapped-{failure.DetailCode}" : "mapped-user-cancellation";
        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            Observed.TrySetResult(exception);
            return ReferenceEquals(exception, expected)
                ? new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, ExpectedMessage)
                : new SharpLinkException(SharpLinkErrorCode.Internal, "wrong logical exception");
        }
    }

    private sealed class FailureStream(Exception exception, int boundary) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCalls;
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => boundary == 1 ? throw exception : this;
        public ValueTask<bool> MoveNextAsync()
            => boundary == 2 ? ValueTask.FromResult(false) : ValueTask.FromException<bool>(exception);
        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            DisposeEntered.TrySetResult();
            return boundary == 2 ? ValueTask.FromException(exception) : new ValueTask(DisposeRelease.Task);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private readonly SharpLinkServer _server;
        private readonly RpcSession _session;
        private readonly ServerGeneratedBridge _bridge;
        private readonly ManualTimeProvider _clock = new();
        internal IdentityMapper Mapper { get; }

        internal Fixture(Exception original)
        {
            Mapper = new IdentityMapper(original);
            _server = (SharpLinkServer)SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableAutomaticServiceRegistration().UseExceptionMapper(Mapper)
                .UseTransport(new IdleListener()).Build();
            var runtime = (SharpLinkRuntimeContext)typeof(SharpLinkServer).GetField(
                "_runtimeContext", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_server)!;
            _session = RpcSessionTestFixture.CreateSessionOverTestTransport("logical-mapper-ownership",
                _input.Reader, _output.Writer, RpcSessionTestFixture.ServerOptions(runtime));
            _bridge = new ServerGeneratedBridge(_server, _session, new StripedLongMap<ServerCallCancellationState>());
        }

        internal Task Pump(IAsyncEnumerable<int> stream)
        {
            var method = new RpcMethodDescriptor(101, 202, RpcMethodKind.ServerStreaming, true, false, true,
                TimeSpan.FromMinutes(5));
            using var scope = SharpLinkCallContext.Push(new SharpLinkServerInvocationContext(method, 73, _session.Id,
                null, null, null, RpcDeadline.Create(TimeSpan.FromMinutes(5), _clock), _clock, null, CancellationToken.None));
            return _bridge.PumpOutboundStreamAsync(73, 0, stream, _session.RuntimeContext.Codecs.GetCodec<int>(),
                false, 101, 202, CancellationToken.None).AsTask();
        }

        internal async Task AssertMappedWireAsync()
        {
            await _session.FlushSendQueueAsync().AsTask().WaitAsync(Watchdog);
            var read = await _output.Reader.ReadAsync().AsTask().WaitAsync(Watchdog);
            var remaining = read.Buffer;
            var errors = 0;
            while (ProtocolV2FrameParser.TryReadFrame(ref remaining, _session.RuntimeContext.Protocol, out var header, out var payload))
            {
                if (header.Type != ProtocolV2FrameType.StreamComplete || (header.Flags & ProtocolV2FrameFlags.Error) == 0)
                    continue;
                var error = ProtocolV2PayloadCodec.ReadError(payload.Slice(sizeof(ushort)), header.Flags,
                    _session.RuntimeContext.Protocol.MaxErrorMessageBytes);
                Ensure(error.Code == SharpLinkErrorCode.FailedPrecondition && error.Message == Mapper.ExpectedMessage,
                    "the original logical exception must select the same wire code and detail-derived message");
                errors++;
            }
            _output.Reader.AdvanceTo(read.Buffer.End);
            Ensure(errors == 1 && remaining.IsEmpty, "one complete mapped error frame is published");
        }

        public async ValueTask DisposeAsync()
        {
            await _session.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
            await _server.DisposeAsync();
        }
    }

    private sealed class IdleListener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
