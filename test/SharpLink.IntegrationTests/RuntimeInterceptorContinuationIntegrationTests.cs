using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class RuntimeInterceptorContinuationIntegrationTests
{
    [Test]
    public async Task ClientReplacementAfterDownstreamNextStartsShouldRetainCapturedGeneration()
    {
        await using var harness = await RuntimeInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new AwaitingClientInterceptor("A", log);
        var b = new AwaitingClientInterceptor("B", log);
        var x = new AwaitingClientInterceptor("X", log);
        var y = new AwaitingClientInterceptor("Y", log);
        harness.Client.ReplaceInterceptors([a, b]);

        var requestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = InvokeClientStreamingAsync(harness.Service, requestRelease.Task);
        await b.NextStarted.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(!first.IsCompleted,
            "client-streaming invocation must remain pending after the downstream interceptor invoked next");

        harness.Client.ReplaceInterceptors([x, y]);
        requestRelease.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(a.Completed, b.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(log, "A:before", "B:before", "B:after", "A:after");

        Clear(log);
        await InvokeClientStreamingAsync(harness.Service, Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(x.Completed, y.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(log, "X:before", "Y:before", "Y:after", "X:after");
    }

    [Test]
    public async Task ServerReplacementAfterDownstreamNextStartsShouldRetainCapturedGeneration()
    {
        await using var harness = await RuntimeInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new AwaitingServerInterceptor("A", log);
        var b = new AwaitingServerInterceptor("B", log);
        var x = new AwaitingServerInterceptor("X", log);
        var y = new AwaitingServerInterceptor("Y", log);
        harness.Server.ReplaceInterceptors([a, b]);

        var requestRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = InvokeClientStreamingAsync(harness.Service, requestRelease.Task);
        await b.NextStarted.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(!first.IsCompleted,
            "server client-streaming invocation must remain pending after the downstream interceptor invoked next");

        harness.Server.ReplaceInterceptors([x, y]);
        requestRelease.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(a.Completed, b.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(log, "A:before", "B:before", "B:after", "A:after");

        Clear(log);
        await InvokeClientStreamingAsync(harness.Service, Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        await Task.WhenAll(x.Completed, y.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(log, "X:before", "Y:before", "Y:after", "X:after");
    }

    [Test]
    public async Task BuildConfiguredInterceptorsShouldBeDisableableWithEmptyRuntimeSnapshot()
    {
        var clientLog = new ConcurrentQueue<string>();
        var serverLog = new ConcurrentQueue<string>();
        var initialClient = new AwaitingClientInterceptor("client-build", clientLog);
        var initialServer = new AwaitingServerInterceptor("server-build", serverLog);
        await using var harness = await RuntimeInterceptorHarness.CreateAsync(initialClient, initialServer);

        Ensure(await harness.Service.DescribeNumberAsync(1) == 2, "build-configured interceptor result");
        await Task.WhenAll(initialClient.Completed, initialServer.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(clientLog, "client-build:before", "client-build:after");
        EnsureSequence(serverLog, "server-build:before", "server-build:after");

        Clear(clientLog);
        Clear(serverLog);
        harness.Client.ReplaceInterceptors([]);
        harness.Server.ReplaceInterceptors([]);

        Ensure(await harness.Service.DescribeNumberAsync(2) == 3, "disabled build generation result");
        Ensure(clientLog.IsEmpty, "empty runtime client snapshot must disable the Build interceptor generation");
        Ensure(serverLog.IsEmpty, "empty runtime server snapshot must disable the Build interceptor generation");
    }

    private static async Task InvokeClientStreamingAsync(IInterceptorTestService service, Task requestRelease)
    {
        Ensure(await service.SumStreamAsync(
            SingleValue(requestRelease),
            CancellationToken.None) == 10,
            "client-streaming result");
    }

    private static async IAsyncEnumerable<int> SingleValue(Task release)
    {
        await release.ConfigureAwait(false);
        yield return 10;
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

    private sealed class AwaitingClientInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _nextStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task NextStarted => _nextStarted.Task;
        public Task Completed => _completed.Task;

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            try
            {
                var invocation = next(context);
                _nextStarted.TrySetResult();
                return await invocation.ConfigureAwait(false);
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class AwaitingServerInterceptor(string id, ConcurrentQueue<string> log)
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

    private sealed class RuntimeInterceptorHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }
        public IInterceptorTestService Service { get; }

        private RuntimeInterceptorHarness(
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

        public static async Task<RuntimeInterceptorHarness> CreateAsync(
            ISharpLinkClientInterceptor? clientInterceptor = null,
            ISharpLinkServerInterceptor? serverInterceptor = null)
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (serverInterceptor is not null)
                serverBuilder.AddInterceptor(serverInterceptor);

            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);

            var clientBuilder = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            if (clientInterceptor is not null)
                clientBuilder.AddInterceptor(clientInterceptor);

            var client = clientBuilder.Build();
            await client.ConnectAsync(cts.Token);
            return new RuntimeInterceptorHarness(cts, serverTask, server, client);
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
