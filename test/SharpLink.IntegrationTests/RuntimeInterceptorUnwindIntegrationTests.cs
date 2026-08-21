using System.Collections.Concurrent;

namespace SharpLink.IntegrationTests;

public class RuntimeInterceptorUnwindIntegrationTests
{
    [Test]
    public async Task ClientFaultUnwindShouldRetainCapturedGenerationAcrossReplacement()
    {
        await using var harness = await UnwindHarness.CreateAsync();
        var oldLog = new ConcurrentQueue<string>();
        var newLog = new ConcurrentQueue<string>();
        var a = new UnwindClientInterceptor("A", oldLog);
        var b = new UnwindClientInterceptor("B", oldLog);
        var c = new GatedFaultUnwindClientInterceptor("C", oldLog);
        harness.Client.ReplaceInterceptors([a, b, c]);

        var call = harness.Service.FailAsync().AsTask();
        await c.UnwindObserved.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(!call.IsCompleted,
            "client fault must remain gated inside the captured interceptor unwind");
        EnsureSequence(oldLog, "A:before", "B:before", "C:before", "C:catch");

        harness.Client.ReplaceInterceptors([
            new UnwindClientInterceptor("X", newLog),
            new UnwindClientInterceptor("Y", newLog),
            new UnwindClientInterceptor("Z", newLog)]);
        c.Release();

        var failure = await CaptureExceptionAsync(call).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Internal },
            "client fault must retain the mapped terminal failure");
        await Task.WhenAll(a.Completed, b.Completed, c.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(
            oldLog,
            "A:before", "B:before", "C:before", "C:catch", "C:after",
            "B:catch", "B:after", "A:catch", "A:after");
        Ensure(newLog.IsEmpty,
            "replacement generation must not enter while the old client fault unwinds");
    }

    [Test]
    public async Task ServerCancellationUnwindShouldRetainCapturedGenerationAcrossReplacement()
    {
        await using var harness = await UnwindHarness.CreateAsync();
        var oldLog = new ConcurrentQueue<string>();
        var newLog = new ConcurrentQueue<string>();
        var a = new UnwindServerInterceptor("A", oldLog);
        var b = new UnwindServerInterceptor("B", oldLog);
        var c = new GatedCancellationUnwindServerInterceptor("C", oldLog);
        harness.Server.ReplaceInterceptors([a, b, c]);

        using var cancellation = new CancellationTokenSource();
        await using var stream = harness.Service.WaitStreamAsync(cancellation.Token).GetAsyncEnumerator();
        Ensure(await stream.MoveNextAsync() && stream.Current == 1,
            "cancellable server stream first item");

        var pendingMove = stream.MoveNextAsync().AsTask();
        await cancellation.CancelAsync();
        await c.UnwindObserved.WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(oldLog, "A:before", "B:before", "C:before", "C:catch");

        harness.Server.ReplaceInterceptors([
            new UnwindServerInterceptor("X", newLog),
            new UnwindServerInterceptor("Y", newLog),
            new UnwindServerInterceptor("Z", newLog)]);
        c.Release();

        var failure = await CaptureExceptionAsync(pendingMove).WaitAsync(TimeSpan.FromSeconds(5));
        Ensure(failure is SharpLinkException { Code: SharpLinkErrorCode.Cancelled },
            "server cancellation must retain the cancelled wire status");
        await Task.WhenAll(a.Completed, b.Completed, c.Completed).WaitAsync(TimeSpan.FromSeconds(3));
        EnsureSequence(
            oldLog,
            "A:before", "B:before", "C:before", "C:catch", "C:after",
            "B:catch", "B:after", "A:catch", "A:after");
        Ensure(newLog.IsEmpty,
            "replacement generation must not enter while the old server cancellation unwinds");
    }

    private static async Task<Exception?> CaptureExceptionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
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

    private static void Ensure(bool condition, string name)
    {
        if (!condition)
            throw new Exception($"assert failed: {name}");
    }

    private sealed class UnwindClientInterceptor(string id, ConcurrentQueue<string> log)
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
            catch
            {
                log.Enqueue($"{id}:catch");
                throw;
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class GatedFaultUnwindClientInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkClientInterceptor
    {
        private readonly TaskCompletionSource _unwindObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task UnwindObserved => _unwindObserved.Task;
        public Task Completed => _completed.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context,
            SharpLinkClientInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            try
            {
                return await next(context).ConfigureAwait(false);
            }
            catch
            {
                log.Enqueue($"{id}:catch");
                _unwindObserved.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw;
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class UnwindServerInterceptor(string id, ConcurrentQueue<string> log)
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
            catch
            {
                log.Enqueue($"{id}:catch");
                throw;
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class GatedCancellationUnwindServerInterceptor(string id, ConcurrentQueue<string> log)
        : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource _unwindObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task UnwindObserved => _unwindObserved.Task;
        public Task Completed => _completed.Task;
        public void Release() => _release.TrySetResult();

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            log.Enqueue($"{id}:before");
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch
            {
                log.Enqueue($"{id}:catch");
                _unwindObserved.TrySetResult();
                await _release.Task.ConfigureAwait(false);
                throw;
            }
            finally
            {
                log.Enqueue($"{id}:after");
                _completed.TrySetResult();
            }
        }
    }

    private sealed class UnwindHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCts;
        private readonly Task _serverTask;

        public ISharpLinkServer Server { get; }
        public ISharpLinkClient Client { get; }
        public IInterceptorTestService Service { get; }

        private UnwindHarness(
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

        public static async Task<UnwindHarness> CreateAsync()
        {
            var cts = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2));
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = Task.Run(() => server.RunAsync(cts.Token).AsTask(), CancellationToken.None);

            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
                .Build();
            await client.ConnectAsync(cts.Token);
            return new UnwindHarness(cts, serverTask, server, client);
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
