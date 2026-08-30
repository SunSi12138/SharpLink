using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class DynamicInterceptorIntegrationTests
{
    [Test]
    public async Task ClientReplacementShouldEnableDisableCopyAndRejectInvalidCandidates()
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new RecordingClientInterceptor("A", log);
        var b = new RecordingClientInterceptor("B", log);
        ISharpLinkClientInterceptor[] source = [a];

        harness.Client.ReplaceInterceptors(source);
        source[0] = b;
        Ensure(await harness.Service.DescribeNumberAsync(1) == 2, "client enabled result");
        EnsureSequence(log, "A:before", "A:after");

        Clear(log);
        harness.Client.ReplaceInterceptors([]);
        Ensure(await harness.Service.DescribeNumberAsync(2) == 3, "client disabled result");
        Ensure(log.IsEmpty, "client disabled pipeline must bypass interceptors");

        harness.Client.ReplaceInterceptors([b]);
        ISharpLinkClientInterceptor[] invalid = [a, null!];
        Ensure(Capture(() => harness.Client.ReplaceInterceptors(invalid)) is ArgumentException,
            "client null candidate rejection");
        Clear(log);
        Ensure(await harness.Service.DescribeNumberAsync(3) == 4, "client old snapshot after invalid update");
        EnsureSequence(log, "B:before", "B:after");

        await harness.Client.StopAsync();
        Ensure(Capture(() => harness.Client.ReplaceInterceptors([a])) is InvalidOperationException,
            "client replacement after stop");
    }

    [Test]
    public async Task ServerReplacementShouldEnableDisableCopyAndRejectInvalidCandidates()
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new RecordingServerInterceptor("A", log);
        var b = new RecordingServerInterceptor("B", log);
        ISharpLinkServerInterceptor[] source = [a];

        harness.Server.ReplaceInterceptors(source);
        source[0] = b;
        Ensure(await harness.Service.DescribeNumberAsync(1) == 2, "server enabled result");
        EnsureSequence(log, "A:before", "A:after");

        Clear(log);
        harness.Server.ReplaceInterceptors([]);
        Ensure(await harness.Service.DescribeNumberAsync(2) == 3, "server disabled result");
        Ensure(log.IsEmpty, "server disabled pipeline must bypass interceptors");

        harness.Server.ReplaceInterceptors([b]);
        ISharpLinkServerInterceptor[] invalid = [a, null!];
        Ensure(Capture(() => harness.Server.ReplaceInterceptors(invalid)) is ArgumentException,
            "server null candidate rejection");
        Clear(log);
        Ensure(await harness.Service.DescribeNumberAsync(3) == 4, "server old snapshot after invalid update");
        EnsureSequence(log, "B:before", "B:after");

        await harness.Server.StopAsync(TimeSpan.Zero);
        Ensure(Capture(() => harness.Server.ReplaceInterceptors([a])) is InvalidOperationException,
            "server replacement after stop");
    }

    [Test]
    [Arguments("unary")]
    [Arguments("oneway")]
    [Arguments("client-streaming")]
    [Arguments("server-streaming")]
    [Arguments("duplex-streaming")]
    public async Task ClientLogicalRpcShouldRetainCapturedGeneration(string shape)
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new BlockingClientInterceptor("A", log);
        var b = new RecordingClientInterceptor("B", log);
        var x = new RecordingClientInterceptor("X", log);
        var y = new RecordingClientInterceptor("Y", log);
        harness.Client.ReplaceInterceptors([a, b]);

        var first = InvokeShapeAsync(harness.Service, shape);
        await a.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        harness.Client.ReplaceInterceptors([x, y]);
        a.Release();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        EnsureSequence(log, "A:before", "B:before", "B:after", "A:after");

        Clear(log);
        await InvokeShapeAsync(harness.Service, shape).WaitAsync(TimeSpan.FromSeconds(5));
        EnsureSequence(log, "X:before", "Y:before", "Y:after", "X:after");
    }

    [Test]
    [Arguments("unary")]
    [Arguments("oneway")]
    [Arguments("client-streaming")]
    [Arguments("server-streaming")]
    [Arguments("duplex-streaming")]
    public async Task ServerLogicalRpcShouldRetainCapturedGeneration(string shape)
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new BlockingServerInterceptor("A", log);
        var b = new RecordingServerInterceptor("B", log);
        var x = new RecordingServerInterceptor("X", log);
        var y = new RecordingServerInterceptor("Y", log);
        harness.Server.ReplaceInterceptors([a, b]);

        var firstInput = CreateInputRelease(shape);
        var first = InvokeShapeAsync(harness.Service, shape, firstInput.Task);
        await a.Entered.WaitAsync(TimeSpan.FromSeconds(3));
        harness.Server.ReplaceInterceptors([x, y]);
        a.Release();
        await b.NextStarted.WaitAsync(TimeSpan.FromSeconds(3));
        firstInput.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await a.Completed.WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(log, "A:before", "B:before", "B:after", "A:after");

        Clear(log);
        var secondInput = CreateInputRelease(shape);
        var second = InvokeShapeAsync(harness.Service, shape, secondInput.Task);
        await y.NextStarted.WaitAsync(TimeSpan.FromSeconds(3));
        secondInput.TrySetResult();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await x.Completed.WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(log, "X:before", "Y:before", "Y:after", "X:after");
    }

    [Test]
    public async Task TelemetryPathShouldUseOuterClientSnapshot()
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var oldInterceptor = new RecordingClientInterceptor("old", log);
        var newInterceptor = new RecordingClientInterceptor("new", log);
        harness.Client.ReplaceInterceptors([oldInterceptor]);
        var switched = 0;

        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "SharpLink.Client",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
            {
                if (Interlocked.Exchange(ref switched, 1) == 0)
                    harness.Client.ReplaceInterceptors([newInterceptor]);
                return ActivitySamplingResult.AllDataAndRecorded;
            }
        };
        ActivitySource.AddActivityListener(listener);

        Ensure(await harness.Service.DescribeNumberAsync(1) == 2, "telemetry old generation result");
        EnsureSequence(log, "old:before", "old:after");

        Clear(log);
        Ensure(await harness.Service.DescribeNumberAsync(2) == 3, "telemetry new generation result");
        EnsureSequence(log, "new:before", "new:after");
    }

    [Test]
    public async Task ReplacementShouldNotDisposeRemovedInterceptors()
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var client = new DisposableClientInterceptor();
        var server = new DisposableServerInterceptor();
        harness.Client.ReplaceInterceptors([client]);
        harness.Server.ReplaceInterceptors([server]);

        harness.Client.ReplaceInterceptors([]);
        harness.Server.ReplaceInterceptors([]);

        Ensure(client.DisposeCount == 0, "client interceptor ownership remains caller-owned");
        Ensure(server.DisposeCount == 0, "server interceptor ownership remains caller-owned");
        await harness.Service.DescribeNumberAsync(1);
        Ensure(client.DisposeCount == 0 && server.DisposeCount == 0, "replacement must not dispose removed interceptors");
    }

    [Test]
    public async Task ConcurrentCallsAndFullReplacementsShouldRemainStable()
    {
        await using var harness = await DynamicInterceptorHarness.CreateAsync();
        var clientA = new PassThroughClientInterceptor();
        var clientB = new PassThroughClientInterceptor();
        var serverA = new PassThroughServerInterceptor();
        var serverB = new PassThroughServerInterceptor();
        var failures = new ConcurrentQueue<Exception>();

        var clientUpdater = Task.Run(() =>
        {
            try
            {
                for (var index = 0; index < 500; index++)
                {
                    harness.Client.ReplaceInterceptors((index % 4) switch
                    {
                        0 => [],
                        1 => [clientA],
                        2 => [clientA, clientB],
                        _ => [clientB]
                    });
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        });

        var serverUpdater = Task.Run(() =>
        {
            try
            {
                for (var index = 0; index < 500; index++)
                {
                    harness.Server.ReplaceInterceptors((index % 4) switch
                    {
                        0 => [],
                        1 => [serverA],
                        2 => [serverA, serverB],
                        _ => [serverB]
                    });
                }
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        });

        var workers = Enumerable.Range(0, 8).Select(async worker =>
        {
            try
            {
                for (var index = 0; index < 50; index++)
                    Ensure(await harness.Service.DescribeNumberAsync(worker + index) == worker + index + 1,
                        "concurrent replacement response");
            }
            catch (Exception exception)
            {
                failures.Enqueue(exception);
            }
        }).ToArray();

        await Task.WhenAll(workers.Concat([clientUpdater, serverUpdater])).WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(failures.IsEmpty, failures.TryPeek(out var failure)
            ? $"concurrent replacement failure: {failure}"
            : "concurrent replacement failure");
    }

    private static async Task InvokeShapeAsync(
        IInterceptorTestService service,
        string shape,
        Task? requestStreamRelease = null)
    {
        switch (shape)
        {
            case "unary":
                Ensure(await service.DescribeNumberAsync(10) == 11, "unary shape");
                return;
            case "oneway":
                await service.NotifyAsync(10);
                return;
            case "client-streaming":
                Ensure(await service.SumStreamAsync(
                    SingleValue(requestStreamRelease),
                    CancellationToken.None) == 10,
                    "client-streaming shape");
                return;
            case "server-streaming":
                var count = 0;
                await foreach (var value in service.OptionalNullStreamAsync())
                {
                    Ensure(value is null, "server-streaming value");
                    count++;
                }
                Ensure(count == 1, "server-streaming count");
                return;
            case "duplex-streaming":
                var stream = service.FailDuplexAsync(
                    SingleValue(requestStreamRelease),
                    CancellationToken.None).GetAsyncEnumerator();
                try
                {
                    Ensure(await stream.MoveNextAsync() && stream.Current == 10, "duplex first value");
                    var failure = await CaptureAsync(stream.MoveNextAsync().AsTask());
                    Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
                        "duplex terminal failure");
                }
                finally
                {
                    await stream.DisposeAsync();
                }
                return;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape), shape, null);
        }
    }

    private static TaskCompletionSource CreateInputRelease(string shape)
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (shape is not "client-streaming" and not "duplex-streaming")
            release.TrySetResult();
        return release;
    }

    private static async IAsyncEnumerable<int> SingleValue(Task? release = null)
    {
        if (release is not null)
            await release.ConfigureAwait(false);
        else
            await Task.Yield();
        yield return 10;
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task<Exception?> CaptureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void EnsureSequence(ConcurrentQueue<string> log, params string[] expected)
    {
        var actual = log.ToArray();
        Ensure(actual.SequenceEqual(expected),
            $"pipeline order expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
    }

    private static void Clear(ConcurrentQueue<string> log)
    {
        while (log.TryDequeue(out _))
        {
        }
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class RecordingClientInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkClientInterceptor
    {
        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            var result = await next(context).ConfigureAwait(false);
            log.Enqueue($"{id}:after");
            return result;
        }
    }

    private sealed class BlockingClientInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            var result = await next(context).ConfigureAwait(false);
            log.Enqueue($"{id}:after");
            return result;
        }
    }

    private sealed class RecordingServerInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _nextStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextStarted => _nextStarted.Task;
        public Task Completed => _completed.Task;

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            try
            {
                var invocation = next(context);
                _nextStarted.TrySetResult();
                await invocation.ConfigureAwait(false);
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class BlockingServerInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Entered => _entered.Task;
        public Task Completed => _completed.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class PassThroughClientInterceptor : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
            => next(context);
    }

    private sealed class PassThroughServerInterceptor : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
            => next(context);
    }

    private sealed class DisposableClientInterceptor : ISharpLinkClientInterceptor, IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next) => next(context);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class DisposableServerInterceptor : ISharpLinkServerInterceptor, IDisposable
    {
        private int _disposeCount;
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next) => next(context);
        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }

    private sealed class DynamicInterceptorHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;
        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }
        public IInterceptorTestService Service { get; }

        private DynamicInterceptorHarness(
            CancellationTokenSource serverCts,
            Task serverTask,
            ISharpLinkServer server,
            ISharpLinkClient client)
        {
            _serverCts = serverCts;
            _serverTask = serverTask;
            Server = server;
            Client = client;
            Service = client.Get<IInterceptorTestService>();
        }

        public static async Task<DynamicInterceptorHarness> CreateAsync()
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .DisableRequestTimeout()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .Build();
            await client.ConnectAsync(cts.Token);
            return new DynamicInterceptorHarness(cts, serverTask, server, client);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            await _serverCts.CancelAsync();
            await Server.DisposeAsync();
            await Task.WhenAny(_serverTask, Task.Delay(1000, CancellationToken.None));
            _serverCts.Dispose();
        }
    }
}
