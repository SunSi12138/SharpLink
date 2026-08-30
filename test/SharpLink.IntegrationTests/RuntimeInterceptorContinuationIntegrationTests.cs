using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class RuntimeInterceptorContinuationIntegrationTests
{
    [Test]
    public async Task ClientReplacementBeforeDownstreamNextAdvancesShouldRetainCapturedGeneration()
    {
        await using var harness = await RuntimeInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new AwaitingClientInterceptor("A", log);
        var b = new GatedNextClientInterceptor("B", log);
        var c = new AwaitingClientInterceptor("C", log);
        var x = new AwaitingClientInterceptor("X", log);
        var y = new AwaitingClientInterceptor("Y", log);
        var z = new AwaitingClientInterceptor("Z", log);
        harness.Client.ReplaceInterceptors([a, b, c]);

        var first = InvokeClientStreamingAsync(harness.Service, Task.CompletedTask);
        await b.Entered;
        Ensure(!first.IsCompleted,
            "client-streaming invocation must remain pending before the delayed interceptor advances to next");
        EnsureSequence(log, "A:before", "B:before");

        harness.Client.ReplaceInterceptors([x, y, z]);
        b.Release();
        await first;
        await Task.WhenAll(a.Completed, b.Completed, c.Completed);
        EnsureSequence(log, "A:before", "B:before", "C:before", "C:after", "B:after", "A:after");

        Clear(log);
        await InvokeClientStreamingAsync(harness.Service, Task.CompletedTask);
        await Task.WhenAll(x.Completed, y.Completed, z.Completed);
        EnsureSequence(log, "X:before", "Y:before", "Z:before", "Z:after", "Y:after", "X:after");
    }

    [Test]
    public async Task ServerReplacementBeforeDownstreamNextAdvancesShouldRetainCapturedGeneration()
    {
        await using var harness = await RuntimeInterceptorHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new AwaitingServerInterceptor("A", log);
        var b = new GatedNextServerInterceptor("B", log);
        var c = new AwaitingServerInterceptor("C", log);
        var x = new AwaitingServerInterceptor("X", log);
        var y = new AwaitingServerInterceptor("Y", log);
        var z = new AwaitingServerInterceptor("Z", log);
        harness.Server.ReplaceInterceptors([a, b, c]);

        var first = InvokeUnaryAsync(harness.Service);
        await b.Entered;
        Ensure(!first.IsCompleted,
            "server unary invocation must remain pending before the delayed interceptor advances to next");
        EnsureSequence(log, "A:before", "B:before");

        harness.Server.ReplaceInterceptors([x, y, z]);
        b.Release();
        await first;
        await Task.WhenAll(a.Completed, b.Completed, c.Completed);
        EnsureSequence(log, "A:before", "B:before", "C:before", "C:after", "B:after", "A:after");

        Clear(log);
        await InvokeUnaryAsync(harness.Service);
        await Task.WhenAll(x.Completed, y.Completed, z.Completed);
        EnsureSequence(log, "X:before", "Y:before", "Z:before", "Z:after", "Y:after", "X:after");
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

    private static async Task InvokeUnaryAsync(IInterceptorTestService service)
    {
        var result = await service.DescribeNumberAsync(9);
        Ensure(result == 10, $"unary result expected 10, actual {result}");
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
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class GatedNextClientInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;
        public Task Completed => _completed.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            _entered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            try
            {
                return await next(context).ConfigureAwait(false);
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
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
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

    private sealed class GatedNextServerInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

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

            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
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
