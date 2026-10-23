using System.IO.Pipelines;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

internal sealed class ServerCallbackOwnershipFixture : IDisposable
{
    internal static readonly TimeSpan Bound = TimeSpan.FromSeconds(5);
    private delegate bool AcceptProgress(
        StripedLongMap<ServerCallCancellationState> calls, long id, out bool owning);
    private static readonly AcceptProgress Accept = typeof(SharpLinkServer)
        .GetMethod("TryAcceptInboundStreamProgress", BindingFlags.Static | BindingFlags.NonPublic)!
        .CreateDelegate<AcceptProgress>();
    private readonly ServerCallCancellationState _call;
    private readonly CancellationTokenRegistration _registration;
    private readonly SharpLinkBufferWriterPool _pool = new(new BufferWriterPoolOptions());
    private bool _parentReleased;
    private bool _triggered;
    private readonly SharpLinkServer? _server;
    private Task _timerWork = Task.CompletedTask;

    internal ServerCallbackOwnershipFixture(ManualTimeProvider? clock = null, Action? callback = null,
        SharpLinkServer? server = null)
    {
        Clock = clock ?? new ManualTimeProvider();
        Connection = CreateConnection(Clock);
        _server = server;
        Ensure(Connection.MarkReady(null), "publish a ready connection");
        Ensure(server is null ? Connection.TryAcquireCall(1) :
            server.TryAcquireCall(Connection) == ServerCallAdmissionResult.Acquired, "own the initial invocation");
        _call = ServerCallCancellationState.Rent(41, RpcDeadline.Create(TimeSpan.FromSeconds(1), Clock),
            Clock, Connection.ConnectionToken, CancellationToken.None, supportsCooperativeCancellation: true);
        _call.AttachPayloadOwner(_pool, Payload);
        _registration = _call.InvocationToken.Register(() =>
        {
            AtCallbackEntry?.Invoke();
            CallbackEntered.TrySetResult();
            try
            {
                CallbackRelease.Wait();
                callback?.Invoke();
            }
            finally
            {
                CallbackExited.TrySetResult();
            }
        });
        Connection.CallCancellations.Set(41, _call);
        Connection.DeadlineScheduler.Register(_call);
    }

    internal Action? AtCallbackEntry { get; set; }
    internal ManualTimeProvider Clock { get; }
    internal ServerConnectionState Connection { get; }
    internal TaskCompletionSource CallbackEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal TaskCompletionSource CallbackExited { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal ManualResetEventSlim CallbackRelease { get; } = new(false);
    internal PayloadOwner Payload { get; } = new();

    internal void Trigger(bool timer)
    {
        _triggered = true;
        if (timer)
            _timerWork = LongRunningTestWorker.Run(() => Clock.Advance(TimeSpan.FromSeconds(1)));
        else
        {
            Clock.AdvanceWithoutRunningTimers(TimeSpan.FromSeconds(1));
            Ensure(!Accept(Connection.CallCancellations, 41, out var owning) && owning,
                "expired StreamData must select its existing deferred notification path");
        }
        LongRunningTestWorker.WaitForPhase(CallbackEntered.Task, Bound);
    }

    internal void ReleaseInvocation()
    {
        if (_parentReleased)
            return;
        _parentReleased = true;
        Ensure(Connection.CallCancellations.TryRemove(41, _call), "remove completed invocation map entry");
        _call.Dispose();
        if (_server is null)
            Connection.ReleaseCall();
        else
            _server.ReleaseCall(Connection);
        Ensure(Connection.ActiveCalls == 0 && Connection.CallCancellations.Count == 0,
            "the callback alone must remain, not an invocation or queued admission");
    }

    public void Dispose()
    {
        CallbackRelease.Set();
        Payload.Release.Set();
        ReleaseInvocation();
        if (_triggered)
        {
            LongRunningTestWorker.WaitForPhase(CallbackExited.Task, Bound);
            LongRunningTestWorker.Join(_timerWork, Bound);
        }
        LongRunningTestWorker.Join(Connection.CloseAsync().AsTask(), Bound);
        LongRunningTestWorker.Join(Connection.ServiceCleanupTask, Bound);
        LongRunningTestWorker.WaitForPhase(Payload.Finished.Task, Bound);
        _registration.Dispose();
        _pool.Dispose();
        CallbackRelease.Dispose();
        Payload.Release.Dispose();
    }

    internal static ServerConnectionState CreateConnection(TimeProvider clock)
    {
        var session = RpcSessionTestFixture.CreateSessionOverTestTransport(Guid.NewGuid().ToString("N"),
            new Pipe().Reader, new Pipe().Writer, RpcSessionTestFixture.ServerOptions());
        return new(session, new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions()),
            CancellationToken.None, clock);
    }

    internal static ServiceRegistration CreateService(TrackingService service)
        => ServiceRegistration.CreateConnection(typeof(object), new Stub(), new ScopeFactory(),
            _ => service, disposeService: true);

    internal static T Field<T>(object instance, string name)
        => (T)instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance)!;

    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    internal sealed class TrackingService : IAsyncDisposable
    {
        internal int DisposeCount;
        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class PayloadOwner : IRpcByteBufferWriter
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(true);
        internal bool ThrowOnDispose;
        internal object? FailurePayload;
        internal int DisposeCount;
        public int WrittenCount => 0;
        public ReadOnlyMemory<byte> WrittenMemory => ReadOnlyMemory<byte>.Empty;
        public Span<byte> WrittenSpan => Span<byte>.Empty;
        public int Capacity => 0;
        public void Clear() { }
        public void Advance(int count) => throw new NotSupportedException();
        public Memory<byte> GetMemory(int sizeHint = 0) => throw new NotSupportedException();
        public Span<byte> GetSpan(int sizeHint = 0) => throw new NotSupportedException();
        public void Dispose()
        {
            Interlocked.Increment(ref DisposeCount);
            Entered.TrySetResult();
            try
            {
                Release.Wait();
                if (ThrowOnDispose)
                {
                    var error = new InvalidOperationException("physical payload cleanup failed");
                    error.Data["payload"] = FailurePayload;
                    FailurePayload = null;
                    throw error;
                }
            }
            finally { Finished.TrySetResult(); }
        }
    }

    private sealed class ScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() => new Scope();
    }
    private sealed class Scope : IServiceScope, IServiceProvider
    {
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => null;
        public void Dispose() { }
    }
    private sealed class Stub : IRpcStub
    {
        public long InterfaceHash => 41;
        public ValueTask InvokeNoReturnAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args) => ValueTask.CompletedTask;
        public ValueTask InvokeNoReturnCancellableAsync(object service, IRpcGeneratedServerBridge bridge,
            long methodHash, long requestId, ReadOnlySequence<byte> args, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
        public ValueTask InvokeAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output) => ValueTask.CompletedTask;
        public ValueTask InvokeCancellableAsync(object service, IRpcGeneratedServerBridge bridge, long methodHash,
            long requestId, ReadOnlySequence<byte> args, IBufferWriter<byte> output, CancellationToken cancellationToken)
            => ValueTask.CompletedTask;
    }
}
