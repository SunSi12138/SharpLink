using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class RuntimeInterceptorReviewCoverageIntegrationTests
{
    [Test]
    public async Task StreamReturningClientCallsShouldCaptureGenerationBeforeEnumeration()
    {
        await using var harness = await ReviewCoverageHarness.CreateAsync();
        var log = new ConcurrentQueue<string>();
        var a = new TraceClientInterceptor("A", log);
        var b = new TraceClientInterceptor("B", log);

        harness.Client.ReplaceInterceptors([a]);
        var oldServerStream = harness.Service.OptionalNullStreamAsync();
        harness.Client.ReplaceInterceptors([b]);
        await DrainOptionalNullStreamAsync(oldServerStream);
        EnsureSequence(log, "A");

        Clear(log);
        var newServerStream = harness.Service.OptionalNullStreamAsync();
        await DrainOptionalNullStreamAsync(newServerStream);
        EnsureSequence(log, "B");

        Clear(log);
        harness.Client.ReplaceInterceptors([a]);
        var oldDuplexStream = harness.Service.FailDuplexAsync(SingleValue(), CancellationToken.None);
        harness.Client.ReplaceInterceptors([b]);
        await DrainFailingDuplexAsync(oldDuplexStream);
        EnsureSequence(log, "A");

        Clear(log);
        var newDuplexStream = harness.Service.FailDuplexAsync(SingleValue(), CancellationToken.None);
        await DrainFailingDuplexAsync(newDuplexStream);
        EnsureSequence(log, "B");
    }

    [Test]
    public async Task ClientReplacementRacingStopShouldCompleteWithoutDeadlockAndRejectAfterStop()
    {
        await using var harness = await ReviewCoverageHarness.CreateAsync();
        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReplacement = new ManualResetEventSlim();
        SetPrivateField(
            harness.Client,
            "_replacementStateGateEnteredForTesting",
            (Action)(() =>
            {
                replacementEntered.TrySetResult();
                releaseReplacement.Wait();
            }));

        var replacementTask = Task.Run(() => Capture(() =>
            harness.Client.ReplaceInterceptors([new PassThroughClientInterceptor()])));
        await replacementEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTask = Task.Run(async () =>
        {
            stopStarted.TrySetResult();
            await harness.Client.StopAsync();
        });
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            Ensure(!replacementTask.IsCompleted,
                "client replacement must remain active while stop starts behind the lifecycle gate");
        }
        finally
        {
            releaseReplacement.Set();
        }

        var replacementFailure = await replacementTask.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(replacementFailure is null,
            "client replacement that already owns the lifecycle gate must complete before stop admission");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(Capture(() => harness.Client.ReplaceInterceptors([new PassThroughClientInterceptor()]))
                is InvalidOperationException,
            "client replacement must reject after stop admission closes");
    }

    [Test]
    public async Task ServerReplacementRacingStopShouldCompleteWithoutDeadlockAndRejectAfterStop()
    {
        await using var harness = await ReviewCoverageHarness.CreateAsync();
        var replacementEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseReplacement = new ManualResetEventSlim();
        SetPrivateField(
            harness.Server,
            "_replacementStateGateEnteredForTesting",
            (Action)(() =>
            {
                replacementEntered.TrySetResult();
                releaseReplacement.Wait();
            }));

        var replacementTask = Task.Run(() => Capture(() =>
            harness.Server.ReplaceInterceptors([new PassThroughServerInterceptor()])));
        await replacementEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopTask = Task.Run(async () =>
        {
            stopStarted.TrySetResult();
            await harness.Server.StopAsync(TimeSpan.Zero);
        });
        await stopStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));

        try
        {
            Ensure(!replacementTask.IsCompleted,
                "server replacement must remain active while stop starts behind the lifecycle gate");
        }
        finally
        {
            releaseReplacement.Set();
        }

        var replacementFailure = await replacementTask.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(replacementFailure is null,
            "server replacement that already owns the lifecycle gate must complete before stop admission");
        await stopTask.WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(Capture(() => harness.Server.ReplaceInterceptors([new PassThroughServerInterceptor()]))
                is InvalidOperationException,
            "server replacement must reject after stop admission closes");
    }

    [Test]
    public async Task ConcurrentClientReplacementsShouldNeverMixPublishedGenerations()
    {
        await using var harness = await ReviewCoverageHarness.CreateAsync();
        var traces = new ConcurrentDictionary<SharpLinkClientInvocationContext, ConcurrentQueue<string>>();
        var a = new CorrelatedClientInterceptor("A", traces);
        var b = new CorrelatedClientInterceptor("B", traces);
        var c = new CorrelatedClientInterceptor("C", traces);
        var d = new CorrelatedClientInterceptor("D", traces);
        harness.Client.ReplaceInterceptors([a, b]);

        var nextRequest = 0;
        var updater = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                harness.Client.ReplaceInterceptors((index & 1) == 0
                    ? [a, b]
                    : [c, d]);
            }
        });

        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var index = 0; index < 50; index++)
            {
                var request = Interlocked.Increment(ref nextRequest);
                Ensure(await harness.Service.DescribeNumberAsync(request) == request + 1,
                    "client mixed-generation stress response");
            }
        }).ToArray();

        await Task.WhenAll(workers.Append(updater)).WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(traces.Count == nextRequest,
            $"every client RPC must have exactly one correlated trace; expected {nextRequest}, actual {traces.Count}");
        foreach (var trace in traces)
        {
            EnsureLegalGeneration(
                trace.Value,
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(trace.Key).ToString());
        }
    }

    [Test]
    public async Task ConcurrentServerReplacementsShouldNeverMixPublishedGenerations()
    {
        await using var harness = await ReviewCoverageHarness.CreateAsync();
        var traces = new ConcurrentDictionary<SharpLinkServerInvocationContext, ConcurrentQueue<string>>();
        var a = new CorrelatedServerInterceptor("A", traces);
        var b = new CorrelatedServerInterceptor("B", traces);
        var c = new CorrelatedServerInterceptor("C", traces);
        var d = new CorrelatedServerInterceptor("D", traces);
        harness.Server.ReplaceInterceptors([a, b]);

        var nextRequest = 0;
        var updater = Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++)
            {
                harness.Server.ReplaceInterceptors((index & 1) == 0
                    ? [a, b]
                    : [c, d]);
            }
        });

        var workers = Enumerable.Range(0, 8).Select(async _ =>
        {
            for (var index = 0; index < 50; index++)
            {
                var request = Interlocked.Increment(ref nextRequest);
                Ensure(await harness.Service.DescribeNumberAsync(request) == request + 1,
                    "server mixed-generation stress response");
            }
        }).ToArray();

        await Task.WhenAll(workers.Append(updater)).WaitAsync(TimeSpan.FromSeconds(15));
        Ensure(traces.Count == nextRequest,
            $"every server RPC must have exactly one correlated trace; expected {nextRequest}, actual {traces.Count}");
        foreach (var trace in traces)
        {
            EnsureLegalGeneration(
                trace.Value,
                System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(trace.Key).ToString());
        }
    }

    private static async Task DrainOptionalNullStreamAsync(IAsyncEnumerable<string?> stream)
    {
        var count = 0;
        await foreach (var value in stream)
        {
            Ensure(value is null, "server-streaming value");
            count++;
        }
        Ensure(count == 1, "server-streaming count");
    }

    private static async Task DrainFailingDuplexAsync(IAsyncEnumerable<int> stream)
    {
        await using var enumerator = stream.GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync() && enumerator.Current == 10,
            "duplex first value");
        var failure = await CaptureAsync(enumerator.MoveNextAsync().AsTask());
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "duplex terminal failure");
    }

    private static async IAsyncEnumerable<int> SingleValue()
    {
        await Task.Yield();
        yield return 10;
    }

    private static void EnsureLegalGeneration(ConcurrentQueue<string> trace, string callId)
    {
        var actual = trace.ToArray();
        var legal = actual.SequenceEqual(["A", "B"]) || actual.SequenceEqual(["C", "D"]);
        Ensure(legal,
            $"call {callId} must match one complete published generation; actual [{string.Join(", ", actual)}]");
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

    private static void SetPrivateField<T>(object target, string fieldName, T value)
    {
        var field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find private field '{fieldName}'");
        field.SetValue(target, value);
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

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class TraceClientInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue(id);
            return next(context);
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

    private sealed class ReviewCoverageHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }
        public IInterceptorTestService Service { get; }

        private ReviewCoverageHarness(
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

        public static async Task<ReviewCoverageHarness> CreateAsync()
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
            return new ReviewCoverageHarness(cts, serverTask, server, client);
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
