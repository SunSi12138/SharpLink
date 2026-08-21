namespace SharpLink.IntegrationTests;

public class RuntimeInterceptorFaultRaceIntegrationTests
{
    [Test]
    public async Task ClientReplacementShouldSerializeWithFaultPublication()
    {
        var transport = new GatedFailClientTransportFactory();
        await using var client = SharpClientBuilder.Create()
            .UseTransport(transport)
            .Build();

        var connectTask = client.ConnectAsync().AsTask();
        await transport.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        var readinessGate = GetPrivateLock(client, "_readinessGate");
        Task<Exception?> replacementTask;
        readinessGate.Enter();
        try
        {
            transport.Fail();
            Thread.Sleep(50);
            replacementTask = Task.Run(() => Capture(() =>
                client.ReplaceInterceptors([new PassThroughClientInterceptor()])));

            Ensure(!replacementTask.Wait(TimeSpan.FromMilliseconds(150)),
                "client replacement must wait for the lifecycle publication gate");
        }
        finally
        {
            readinessGate.Exit();
        }

        var connectFailure = await CaptureAsync(connectTask);
        Ensure(connectFailure is InvalidOperationException { Message: "client connect failed" },
            "client connect failure must surface");
        Ensure(client.State == SharpLinkConnectionState.Faulted,
            "client must publish Faulted after the failed initial connect");

        var replacementFailure = await replacementTask.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(replacementFailure is InvalidOperationException,
            "replacement queued behind the earlier fault publication must be rejected");
    }

    [Test]
    public async Task ServerFaultPublicationShouldSerializeWithReplacementGate()
    {
        var listener = new GatedFailServerTransportListener();
        await using var server = SharpLinkServerBuilder.Create()
            .UseTransport(listener)
            .Build();

        var runTask = server.RunAsync().AsTask();
        await listener.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Ready,
            "server must be running before the injected accept failure");

        var stateGate = GetPrivateLock(server, "_stateGate");
        stateGate.Enter();
        try
        {
            listener.Fail();
            Ensure(listener.Throwing.Task.Wait(TimeSpan.FromSeconds(1)),
                "server listener did not release the injected failure");

            var faultPublishedWhileGateHeld = SpinWait.SpinUntil(
                () => server.HealthStatus != SharpLinkHealthStatus.Ready,
                TimeSpan.FromMilliseconds(250));
            Ensure(!faultPublishedWhileGateHeld,
                "server fault publication must wait for the replacement lifecycle gate");
        }
        finally
        {
            stateGate.Exit();
        }

        var runFailure = await CaptureAsync(runTask);
        Ensure(runFailure is InvalidOperationException { Message: "server accept failed" },
            "server accept failure must surface");
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Unhealthy,
            "server must publish Faulted after the accept loop failure");
        Ensure(Capture(() => server.ReplaceInterceptors([new PassThroughServerInterceptor()]))
                is InvalidOperationException,
            "server replacement after fault must be rejected");
    }

    private static System.Threading.Lock GetPrivateLock(object target, string fieldName)
    {
        var field = target.GetType().GetField(
            fieldName,
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new Exception($"cannot find private lock '{fieldName}'");
        return (System.Threading.Lock)(field.GetValue(target)
            ?? throw new Exception($"private lock '{fieldName}' is null"));
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

    private sealed class GatedFailClientTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Started => _started;

        public void Fail() => _fail.TrySetResult();

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _fail.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException("client connect failed");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class GatedFailServerTransportListener : IServerTransportListener
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _fail =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _throwing =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public EndPoint? LocalEndPoint => null;
        public TaskCompletionSource Started => _started;
        public TaskCompletionSource Throwing => _throwing;

        public void Fail() => _fail.TrySetResult();

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            await _fail.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            _throwing.TrySetResult();
            throw new InvalidOperationException("server accept failed");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
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
}
