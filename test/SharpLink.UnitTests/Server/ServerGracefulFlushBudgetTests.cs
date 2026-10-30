using System.IO.Pipelines;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Server.ServerCallbackOwnershipFixture;

namespace SharpLink.UnitTests.Server;

public sealed class ServerGracefulFlushBudgetTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GracefulFlushMustFinishOrReachOriginalDeadlineBeforeForcedClose(bool expire)
    {
        var clock = new ManualTimeProvider();
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration().UseTimeProvider(clock).UseTransport(new Listener()).Build();
        await server.StartAsync().AsTask().WaitAsync(Bound);
        var transport = new HeldFlushTransport();
        var runtime = Field<SharpLinkRuntimeContext>(server, "_runtimeContext");
        var session = new RpcSession(transport, RpcSessionTestFixture.ServerOptions(runtime));
        RpcSessionTestFixture.CompleteHandshake(session);
        var connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(new RuntimeConcurrencyOptions()),
            CancellationToken.None, clock);
        Ensure(connection.MarkReady(null), "the source connection must be admitted before Stop");
        Ensure(Field<ServerConnectionRegistry>(server, "_connectionRegistry").TryAdd(session.Id, connection),
            "register the actual session in the server stop cohort");
        var graceful = TimeSpan.FromSeconds(3);
        Task? stop = null;
        try
        {
            stop = server.StopAsync(graceful).AsTask();
            await transport.Writer.Entered.Task.WaitAsync(Bound);
            Ensure(SpinWait.SpinUntil(() => clock.EarliestTimerTimestamp == graceful.Ticks || stop.IsCompleted, Bound),
                "flush must be observed at the original absolute graceful deadline");
            Ensure(!stop.IsCompleted && !transport.Writer.CancellationRequested.Task.IsCompleted && transport.DisposeCount == 0,
                "a started but held graceful flush is not permission to close the session");
            // Later stop callers cannot extend the first owner's deadline; canceling one
            // observer cannot cancel the flush or start a second shutdown owner.
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await server.StopAsync(TimeSpan.FromHours(1), canceled.Token); }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { }
            Ensure(!transport.Writer.CancellationRequested.Task.IsCompleted,
                "a canceled public waiter must not force the shared stop owner");
            if (expire)
            {
                clock.Advance(graceful);
                await transport.Writer.CancellationRequested.Task.WaitAsync(Bound);
                Ensure(transport.DisposeCount == 0 && !stop.IsCompleted,
                    "deadline requests abort, but cannot bypass the still-held physical writer");
                var finalDeadline = graceful.Ticks + server.ShutdownPlanForDiagnostics.CleanupBudget.Ticks;
                Ensure(SpinWait.SpinUntil(() => clock.EarliestTimerTimestamp == finalDeadline || stop.IsCompleted, Bound),
                    "forced cleanup must retain the original final absolute deadline");
                clock.Advance(server.ShutdownPlanForDiagnostics.CleanupBudget);
                try { await stop.WaitAsync(Bound); }
                catch (Exception) when (stop.IsCompleted) { _ = stop.Exception; }
                Ensure(stop.IsFaulted && transport.DisposeCount == 0,
                    "public budget expiry is a failure, not physical cleanup completion");
            }
            transport.Writer.Release.TrySetResult();
            if (!expire) await stop.WaitAsync(Bound);
            await connection.SessionTask.WaitAsync(Bound);
            await connection.ServiceCleanupTask.WaitAsync(Bound);
            Ensure(transport.DisposeCount == 1 && transport.Writer.CompleteCount == 1,
                "the physical tail must dispose the provider and writer exactly once");
        }
        finally
        {
            transport.Writer.Release.TrySetResult();
            clock.Advance(graceful + server.ShutdownPlanForDiagnostics.CleanupBudget);
            try { await (stop ?? server.StopAsync(TimeSpan.Zero).AsTask()).WaitAsync(Bound); }
            catch (Exception) when (stop?.IsCompleted == true) { _ = stop.Exception; }
            await connection.CloseAsync().AsTask().WaitAsync(Bound);
            await connection.ServiceCleanupTask.WaitAsync(Bound);
            var lifecycle = Field<SharpLinkServer.ServerLifecycleCoordinator>(server, "_lifecycle");
            foreach (var name in new[] { "_shutdownCleanupObserver", "_serviceCleanupObserver" })
                if (Field<Task?>(lifecycle, name) is { } observer) await observer.WaitAsync(Bound);
        }
    }

    private sealed class Listener : IServerTransportListener
    {
        public System.Net.EndPoint? LocalEndPoint => null;
        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("a canceled accept cannot complete");
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class HeldFlushTransport : ITransportConnection
    {
        private readonly Pipe _input = new();
        internal HeldFlushWriter Writer { get; } = new();
        internal int DisposeCount;
        public string Id => "graceful-flush-budget";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => Writer;
        public System.Net.EndPoint? LocalEndPoint => null;
        public System.Net.EndPoint? RemoteEndPoint => null;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            await Writer.CompleteAsync();
            await _input.Reader.CompleteAsync();
            await _input.Writer.CompleteAsync();
        }
    }

    private sealed class HeldFlushWriter : PipeWriter
    {
        private readonly byte[] _bytes = new byte[4096];
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource CancellationRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CompleteCount;
        public override void Advance(int bytes) { }
        public override Memory<byte> GetMemory(int sizeHint = 0) => _bytes;
        public override Span<byte> GetSpan(int sizeHint = 0) => _bytes;
        public override void CancelPendingFlush() => CancellationRequested.TrySetResult();
        public override void Complete(Exception? exception = null) => Interlocked.Increment(ref CompleteCount);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task; // Deliberately noncooperative: cancellation is not completion.
            return new FlushResult(isCanceled: false, isCompleted: false);
        }
    }
}
