using System.Diagnostics.Metrics;

namespace SharpLink.IntegrationTests;

[NotInParallel]
public sealed class ExtensionFaultContainmentLifecycleRaceTests
{
    [Test]
    public async Task SuspendedMoveNextFaultAfterCallerCancellationShouldPreserveCancelAndReleaseState()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var producer = new SuspendedFaultingStream(throwOnDispose: true);
        using var cancellation = new CancellationTokenSource();
        var call = harness.Service.UploadAsync(producer, cancellation.Token).AsTask();

        await producer.MoveNextEntered.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await cancellation.CancelAsync().ConfigureAwait(false);
        await producer.ProducerTokenCancelled.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        producer.ReleaseFault();

        var failure = await CaptureFailureAsync(call).ConfigureAwait(false);
        Ensure(
            failure is OperationCanceledException ||
            failure is SharpLinkException { Code: SharpLinkErrorCode.Cancelled },
            "caller cancellation must remain the terminal owner when suspended MoveNext and DisposeAsync fault late");
        await producer.Disposed.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Ensure(producer.MoveNextCalls == 1, "cancel race must invoke MoveNext exactly once");
        Ensure(producer.DisposeCalls == 1, "cancel race must dispose the producer exactly once");
        await harness.AssertIdleAsync("fault vs caller cancellation").ConfigureAwait(false);
        await harness.AssertReusableAsync("fault vs caller cancellation").ConfigureAwait(false);
    }

    [Test]
    public async Task SuspendedMoveNextFaultAfterDeadlineShouldPreserveDeadlineAndReleaseState()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var producer = new SuspendedFaultingStream(throwOnDispose: true);
        var call = harness.DeadlineService
            .UploadWithDeadlineAsync(producer, CancellationToken.None)
            .AsTask();

        await producer.MoveNextEntered.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        await producer.ProducerTokenCancelled.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        producer.ReleaseFault();

        var failure = await CaptureFailureAsync(call).ConfigureAwait(false);
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.DeadlineExceeded },
            "deadline must remain the terminal owner when suspended MoveNext and DisposeAsync fault late");
        await producer.Disposed.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Ensure(producer.MoveNextCalls == 1, "deadline race must invoke MoveNext exactly once");
        Ensure(producer.DisposeCalls == 1, "deadline race must dispose the producer exactly once");
        await harness.AssertIdleAsync("fault vs deadline").ConfigureAwait(false);
        await harness.AssertReusableAsync("fault vs deadline").ConfigureAwait(false);
    }

    [Test]
    public async Task SuspendedMoveNextFaultDuringServerStopShouldNotStrandCallOrProducerState()
    {
        await using var harness = await LifecycleHarness.CreateAsync();
        var producer = new SuspendedFaultingStream(throwOnDispose: true);
        var call = harness.Service.UploadAsync(producer, CancellationToken.None).AsTask();

        await producer.MoveNextEntered.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        var stop = harness.StopServerAsync();
        await producer.ProducerTokenCancelled.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        producer.ReleaseFault();

        await stop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        var failure = await CaptureFailureAsync(call).ConfigureAwait(false);
        Ensure(
            failure is SharpLinkException
            {
                Code: SharpLinkErrorCode.ConnectionClosed or
                    SharpLinkErrorCode.Unavailable or
                    SharpLinkErrorCode.Cancelled
            },
            "server stop must own the public terminal instead of late producer MoveNext/DisposeAsync faults");
        await producer.Disposed.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        Ensure(producer.MoveNextCalls == 1, "stop race must invoke MoveNext exactly once");
        Ensure(producer.DisposeCalls == 1, "stop race must dispose the producer exactly once");
        await harness.AssertIdleAsync("fault vs server stop").ConfigureAwait(false);
        Ensure(harness.Server.HealthStatus != SharpLinkHealthStatus.Ready,
            "server must no longer advertise Ready after StopAsync completes");
    }

    [Test]
    public async Task ClientInterceptorMayReenterAnotherRpcWithoutDeadlockOrDuplicateTerminal()
    {
        var interceptor = new ReentrantRpcClientInterceptor();
        var service = new ExtensionFaultService();
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServiceInstance = service,
            ClientInterceptors = [interceptor],
            SkipInitialSessionProbe = true
        });
        interceptor.Client = harness.Client;

        interceptor.ReentryEnabled = false;
        var sessionBefore = await harness.Service.GetSessionIdAsync().ConfigureAwait(false);
        var invocationsBefore = service.InvocationCount;
        var entriesBefore = interceptor.Entries;
        interceptor.ReentryEnabled = true;

        var result = await harness.Service.EchoAsync(41)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);

        Ensure(result == 42, "outer RPC result after interceptor reentrancy");
        Ensure(interceptor.Entries == entriesBefore + 2,
            "outer and nested RPC should each enter the interceptor exactly once after the baseline probe");
        Ensure(interceptor.NestedCalls == 1 && interceptor.NestedResult == 2,
            "interceptor must perform exactly one guarded nested RPC");
        Ensure(service.InvocationCount == invocationsBefore + 2,
            "reentrant interceptor must produce exactly one nested and one outer service invocation");

        interceptor.ReentryEnabled = false;
        await harness.AssertClientIdleAsync("client interceptor RPC reentrancy").ConfigureAwait(false);
        var sessionAfter = await harness.Service.GetSessionIdAsync().ConfigureAwait(false);
        Ensure(string.Equals(sessionBefore, sessionAfter, StringComparison.Ordinal),
            "reentrant interceptor must not poison or replace the physical session");
        Ensure(await harness.Service.EchoAsync(99).ConfigureAwait(false) == 100,
            "healthy RPC must succeed immediately after interceptor reentrancy");
    }

    [Test]
    public async Task CompletionMetricCallbackMayReenterClientLifecycleApiWithoutDeadlock()
    {
        await using var harness = await ExtensionFaultHarness.CreateAsync();
        using var listener = new ReentrantCompletionMeterScope(harness.Client);

        var result = await harness.Service.EchoAsync(41)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(3))
            .ConfigureAwait(false);
        await listener.CallbackReturned.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);

        Ensure(result == 42, "metric reentrancy must not replace the business result");
        Ensure(listener.ReentryCount == 1, "completion callback must reenter exactly once");
        Ensure(listener.ReentryFailure is null,
            $"client lifecycle reentry from MeterListener failed: {listener.ReentryFailure}");
        await harness.AssertReusableAsync("MeterListener lifecycle reentrancy").ConfigureAwait(false);
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class SuspendedFaultingStream(bool throwOnDispose)
        : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private readonly TaskCompletionSource<bool> _moveNextEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _producerTokenCancelled =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _releaseFault =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _disposed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenRegistration _registration;
        private int _moveNextCalls;
        private int _disposeCalls;

        internal Task MoveNextEntered => _moveNextEntered.Task;
        internal Task ProducerTokenCancelled => _producerTokenCancelled.Task;
        internal Task Disposed => _disposed.Task;
        internal int MoveNextCalls => Volatile.Read(ref _moveNextCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        public int Current => 7;

        internal void ReleaseFault() => _releaseFault.TrySetResult(true);

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            _registration = cancellationToken.Register(
                static state => ((SuspendedFaultingStream)state!)._producerTokenCancelled.TrySetResult(true),
                this);
            return this;
        }

        public async ValueTask<bool> MoveNextAsync()
        {
            var call = Interlocked.Increment(ref _moveNextCalls);
            if (call != 1)
                return false;

            _moveNextEntered.TrySetResult(true);
            await _releaseFault.Task.ConfigureAwait(false);
            throw new InvalidOperationException("injected suspended MoveNext fault");
        }

        public ValueTask DisposeAsync()
        {
            _registration.Dispose();
            if (Interlocked.Increment(ref _disposeCalls) == 1)
                _disposed.TrySetResult(true);
            return throwOnDispose
                ? ValueTask.FromException(new InvalidOperationException("injected producer DisposeAsync failure"))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class ReentrantRpcClientInterceptor : ISharpLinkClientInterceptor
    {
        private readonly AsyncLocal<int> _depth = new();
        private int _entries;
        private int _nestedCalls;
        private int _reentryEnabled = 1;
        private int _nestedResult;

        internal ISharpLinkClient? Client { get; set; }
        internal int Entries => Volatile.Read(ref _entries);
        internal int NestedCalls => Volatile.Read(ref _nestedCalls);
        internal int NestedResult => Volatile.Read(ref _nestedResult);
        internal bool ReentryEnabled
        {
            get => Volatile.Read(ref _reentryEnabled) != 0;
            set => Volatile.Write(ref _reentryEnabled, value ? 1 : 0);
        }

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            Interlocked.Increment(ref _entries);
            if (ReentryEnabled && _depth.Value == 0)
            {
                _depth.Value = 1;
                try
                {
                    var client = Client ?? throw new InvalidOperationException("reentrant client not initialized");
                    var nested = await client.Get<IExtensionFaultService>()
                        .EchoAsync(1)
                        .ConfigureAwait(false);
                    Volatile.Write(ref _nestedResult, nested);
                    Interlocked.Increment(ref _nestedCalls);
                }
                finally
                {
                    _depth.Value = 0;
                }
            }

            return await next(context).ConfigureAwait(false);
        }
    }

    private sealed class ReentrantCompletionMeterScope : IDisposable
    {
        private readonly MeterListener _listener = new();
        private readonly ISharpLinkClient _client;
        private readonly TaskCompletionSource<bool> _callbackReturned =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _remaining = 1;
        private int _reentryCount;
        private Exception? _reentryFailure;

        internal ReentrantCompletionMeterScope(ISharpLinkClient client)
        {
            _client = client;
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter) &&
                    instrument.Name == "sharplink.calls.completed")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                if (!HasSide(tags, "client") || Interlocked.Exchange(ref _remaining, 0) == 0)
                    return;

                try
                {
                    _client.ReplaceInterceptors([]);
                    Interlocked.Increment(ref _reentryCount);
                }
                catch (Exception exception)
                {
                    _reentryFailure = exception;
                }
                finally
                {
                    _callbackReturned.TrySetResult(true);
                }
            });
            _listener.Start();
        }

        internal Task CallbackReturned => _callbackReturned.Task;
        internal int ReentryCount => Volatile.Read(ref _reentryCount);
        internal Exception? ReentryFailure => _reentryFailure;

        public void Dispose() => _listener.Dispose();

        private static bool HasSide(ReadOnlySpan<KeyValuePair<string, object?>> tags, string side)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && string.Equals(tag.Value as string, side, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }
    }

    private sealed class LifecycleHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;
        private readonly string _initialSession;
        private int _serverStopped;

        private LifecycleHarness(
            CancellationTokenSource serverCancellation,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client,
            string initialSession)
        {
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
            Server = server;
            Client = client;
            Service = client.Get<IExtensionFaultService>();
            DeadlineService = client.Get<IExtensionDeadlineFaultService>();
            _initialSession = initialSession;
        }

        internal ISharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }
        internal IExtensionFaultService Service { get; }
        internal IExtensionDeadlineFaultService DeadlineService { get; }

        internal static async Task<LifecycleHarness> CreateAsync()
        {
            var cancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
                .ReplaceService<IExtensionFaultService>(new ExtensionFaultService());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(async () =>
            {
                try
                {
                    await server.RunUntilStoppedAsync(cancellation.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }, CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
                .DisableRequestTimeout()
                .Build();
            try
            {
                await client.ConnectAsync(cancellation.Token).ConfigureAwait(false);
                var service = client.Get<IExtensionFaultService>();
                var session = await service.GetSessionIdAsync().ConfigureAwait(false);
                return new LifecycleHarness(cancellation, serverTask, server, client, session);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                await cancellation.CancelAsync().ConfigureAwait(false);
                await server.DisposeAsync().ConfigureAwait(false);
                cancellation.Dispose();
                throw;
            }
        }

        internal async Task StopServerAsync()
        {
            if (Interlocked.Exchange(ref _serverStopped, 1) != 0)
                return;
            await Server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        }

        internal async Task AssertIdleAsync(string scenario)
        {
            var client = (SharpLinkClient)Client;
            var started = Stopwatch.GetTimestamp();
            while (client.PendingCallCount != 0 ||
                   client.ActiveClientCallCount != 0 ||
                   client.ActiveClientStreamCount != 0 ||
                   ServerCallAdmissionDiagnostics.ActiveCallCount(Server) != 0 ||
                   ServerCallAdmissionDiagnostics.PendingCallAdmissions(Server) != 0)
            {
                if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromSeconds(3))
                {
                    throw new Exception(
                        $"assert failed: {scenario}: resources did not return to baseline; " +
                        $"pending={client.PendingCallCount} clientCalls={client.ActiveClientCallCount} " +
                        $"clientStreams={client.ActiveClientStreamCount} " +
                        $"serverCalls={ServerCallAdmissionDiagnostics.ActiveCallCount(Server)} " +
                        $"serverAdmissions={ServerCallAdmissionDiagnostics.PendingCallAdmissions(Server)}");
                }
                await Task.Yield();
            }
        }

        internal async Task AssertReusableAsync(string scenario)
        {
            await AssertIdleAsync(scenario).ConfigureAwait(false);
            var session = await Service.GetSessionIdAsync().ConfigureAwait(false);
            Ensure(string.Equals(_initialSession, session, StringComparison.Ordinal),
                $"{scenario}: physical session changed after lifecycle race");
            Ensure(await Service.EchoAsync(41).ConfigureAwait(false) == 42,
                $"{scenario}: healthy RPC failed after lifecycle race");
            await AssertIdleAsync(scenario + " after reuse").ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync().ConfigureAwait(false);
            await _serverCancellation.CancelAsync().ConfigureAwait(false);
            await Server.DisposeAsync().ConfigureAwait(false);
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            _serverCancellation.Dispose();
        }
    }
}

[RpcContract]
public interface IExtensionDeadlineFaultService : IService
{
    [SharpLink.Sdk.Timeout(0.12)]
    ValueTask<int> UploadWithDeadlineAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default);
}

[RpcService]
public sealed class ExtensionDeadlineFaultService : IExtensionDeadlineFaultService
{
    public async ValueTask<int> UploadWithDeadlineAsync(
        IAsyncEnumerable<int> values,
        CancellationToken cancellationToken = default)
    {
        var sum = 0;
        await foreach (var value in values.WithCancellation(cancellationToken).ConfigureAwait(false))
            sum += value;
        return sum;
    }
}
