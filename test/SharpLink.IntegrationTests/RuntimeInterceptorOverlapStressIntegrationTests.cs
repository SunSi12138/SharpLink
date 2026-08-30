using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class RuntimeInterceptorOverlapStressIntegrationTests
{
    [Test]
    public async Task ClientReplacementStressShouldOverlapInFlightRpcsAndPreserveGeneration()
    {
        await using var harness = await OverlapHarness.CreateAsync();
        var traces = new ConcurrentDictionary<SharpLinkClientInvocationContext, ConcurrentQueue<string>>();
        var gate = new ClientOverlapGate();
        var a = new CorrelatedClientInterceptor("A", traces);
        var b = new CorrelatedClientInterceptor("B", traces);
        var c = new CorrelatedClientInterceptor("C", traces);
        var d = new CorrelatedClientInterceptor("D", traces);
        harness.Client.ReplaceInterceptors([gate, a, b]);

        var firstUpdatePublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = Task.Run(async () =>
        {
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(3));
            var index = 0;
            do
            {
                harness.Client.ReplaceInterceptors((index++ & 1) == 0
                    ? [gate, a, b]
                    : [gate, c, d]);
                firstUpdatePublished.TrySetResult();
                await Task.Yield();
            }
            while (gate.EnteredCount < 32);
        });

        var nextRequest = 0;
        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var index = 0; index < 50; index++)
            {
                var request = Interlocked.Increment(ref nextRequest);
                Ensure(await harness.Service.DescribeNumberAsync(request) == request + 1,
                    "client overlap stress response");
            }
        }).ToArray();

        await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(3));
        await firstUpdatePublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(!updater.IsCompleted,
            "client updater must remain active while worker RPCs are blocked in the overlap gate");
        gate.Release();

        await Task.WhenAll(workers.Append(updater)).WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(gate.EnteredCount >= 32,
            "client updater must remain active until multiple worker RPCs have entered");
        Ensure(traces.Count == nextRequest,
            $"every client RPC must have one correlated trace; expected {nextRequest}, actual {traces.Count}");
        foreach (var trace in traces)
            EnsureLegalGeneration(trace.Value, GetCallId(trace.Key));
    }

    [Test]
    public async Task ServerReplacementStressShouldOverlapInFlightRpcsAndPreserveGeneration()
    {
        await using var harness = await OverlapHarness.CreateAsync();
        var traces = new ConcurrentDictionary<SharpLinkServerInvocationContext, ConcurrentQueue<string>>();
        var gate = new ServerOverlapGate();
        var a = new CorrelatedServerInterceptor("A", traces);
        var b = new CorrelatedServerInterceptor("B", traces);
        var c = new CorrelatedServerInterceptor("C", traces);
        var d = new CorrelatedServerInterceptor("D", traces);
        harness.Server.ReplaceInterceptors([gate, a, b]);

        var firstUpdatePublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var updater = Task.Run(async () =>
        {
            await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(3));
            var index = 0;
            do
            {
                harness.Server.ReplaceInterceptors((index++ & 1) == 0
                    ? [gate, a, b]
                    : [gate, c, d]);
                firstUpdatePublished.TrySetResult();
                await Task.Yield();
            }
            while (gate.EnteredCount < 32);
        });

        var nextRequest = 0;
        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var index = 0; index < 50; index++)
            {
                var request = Interlocked.Increment(ref nextRequest);
                Ensure(await harness.Service.DescribeNumberAsync(request) == request + 1,
                    "server overlap stress response");
            }
        }).ToArray();

        await gate.FirstEntered.WaitAsync(TimeSpan.FromSeconds(3));
        await firstUpdatePublished.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(!updater.IsCompleted,
            "server updater must remain active while worker RPCs are blocked in the overlap gate");
        gate.Release();

        await Task.WhenAll(workers.Append(updater)).WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(gate.EnteredCount >= 32,
            "server updater must remain active until multiple worker RPCs have entered");
        Ensure(traces.Count == nextRequest,
            $"every server RPC must have one correlated trace; expected {nextRequest}, actual {traces.Count}");
        foreach (var trace in traces)
            EnsureLegalGeneration(trace.Value, GetCallId(trace.Key));
    }

    private static string GetCallId(object context)
        => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(context).ToString();

    private static void EnsureLegalGeneration(ConcurrentQueue<string> trace, string callId)
    {
        var actual = trace.ToArray();
        var legal = actual.SequenceEqual(["A", "B"]) || actual.SequenceEqual(["C", "D"]);
        Ensure(legal,
            $"call {callId} must match one complete published generation; actual [{string.Join(", ", actual)}]");
    }

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class ClientOverlapGate : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enteredCount;

        public Task FirstEntered => _firstEntered.Task;
        public int EnteredCount => Volatile.Read(ref _enteredCount);

        public void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            if (Interlocked.Increment(ref _enteredCount) == 1)
                _firstEntered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            return await next(context).ConfigureAwait(false);
        }
    }

    private sealed class ServerOverlapGate : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _firstEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _enteredCount;

        public Task FirstEntered => _firstEntered.Task;
        public int EnteredCount => Volatile.Read(ref _enteredCount);

        public void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (Interlocked.Increment(ref _enteredCount) == 1)
                _firstEntered.TrySetResult();
            await _release.Task.ConfigureAwait(false);
            await next(context).ConfigureAwait(false);
        }
    }

    private sealed class CorrelatedClientInterceptor(
        string id,
        ConcurrentDictionary<SharpLinkClientInvocationContext, ConcurrentQueue<string>> traces)
        : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            traces.GetOrAdd(context, static _ => new ConcurrentQueue<string>()).Enqueue(id);
            return next(context);
        }
    }

    private sealed class CorrelatedServerInterceptor(
        string id,
        ConcurrentDictionary<SharpLinkServerInvocationContext, ConcurrentQueue<string>> traces)
        : ISharpLinkServerInterceptor
    {
        public ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            traces.GetOrAdd(context, static _ => new ConcurrentQueue<string>()).Enqueue(id);
            return next(context);
        }
    }

    private sealed class OverlapHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }
        public IInterceptorTestService Service { get; }

        private OverlapHarness(
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

        public static async Task<OverlapHarness> CreateAsync()
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
            return new OverlapHarness(cts, serverTask, server, client);
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
