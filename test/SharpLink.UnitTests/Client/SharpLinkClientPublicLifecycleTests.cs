using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class SharpLinkClientPublicLifecycleTests
{
    [Test]
    public async Task StartAsyncShouldRunWithoutRemoteReadinessAndStopOwnedSupervisor()
    {
        var transport = new FailingTransportFactory();
        var client = ClientBuilderTestHelper.Build(transport);
        try
        {
            await client.StartAsync();

            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "local lifecycle should be Running after StartAsync returns");
            Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
                "remote failure must not be projected as local runtime failure");
            Ensure(client.ClusterState != SharpLinkClusterState.Ready,
                "cluster must not be reported Ready while every connection attempt fails");
            Ensure(Volatile.Read(ref transport.ConnectCount) > 0,
                "the client-owned connectivity supervisor should attempt remote connection");

            using (var readinessCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                await EnsureCancelledAsync(
                    client.WaitForReadyAsync(readinessCancellation.Token).AsTask(),
                    "readiness wait cancellation must cancel only the caller");
            }
            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "cancelling a readiness waiter must not stop the runtime");

            using (var shutdownCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50)))
            {
                await EnsureCancelledAsync(
                    client.WaitForShutdownAsync(shutdownCancellation.Token),
                    "shutdown wait cancellation must cancel only the caller");
            }
            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "WaitForShutdownAsync must not initiate shutdown");

            await client.StopAsync();
            await client.WaitForShutdownAsync();

            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Stopped,
                "StopAsync must transition the local lifecycle to Stopped");
            var frameworkTasks = client.FrameworkTaskSnapshotForDiagnostics;
            Ensure(frameworkTasks.IsSealed && frameworkTasks.IsDrained,
                "StopAsync must seal and drain framework-owned connectivity supervision");
            Ensure(frameworkTasks.ActiveTasks == 0,
                "no framework-owned connectivity task may remain active after StopAsync");
        }
        finally
        {
            await client.DisposeAsync();
        }
    }

    [Test]
    public async Task WaitForReadyAsyncShouldRequireLocalStart()
    {
        await using var client = ClientBuilderTestHelper.Build(new FailingTransportFactory());

        try
        {
            await client.WaitForReadyAsync();
            throw new Exception("expected readiness wait before StartAsync to fail");
        }
        catch (InvalidOperationException)
        {
        }

        Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Created,
            "readiness observation must not implicitly start the local runtime");
    }

    private static async Task EnsureCancelledAsync(Task task, string message)
    {
        try
        {
            await task;
            throw new Exception(message);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class FailingTransportFactory : IClientTransportFactory
    {
        internal int ConnectCount;

        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ConnectCount);
            return ValueTask.FromException<ITransportConnection>(
                new NotSupportedException("remote endpoint is unavailable"));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
