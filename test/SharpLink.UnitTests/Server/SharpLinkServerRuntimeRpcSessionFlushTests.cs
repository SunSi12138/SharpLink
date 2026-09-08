using System.Diagnostics;
using System.Reflection;
using System.Threading;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    [NotInParallel]
    public async Task RpcSessionFlushUpdateMustNotPublishAfterStopOwnsLifecycleGate()
    {
        var listener = new BlockingListener();
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .Build();
        var runTask = server.RunAsync().AsTask();
        await listener.AcceptStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(server.HealthStatus == SharpLinkHealthStatus.Ready,
            "the race fixture must begin from a Running server");

        var initial = server.GetRpcSessionFlushPolicySnapshot();
        var registryGate = GetPrivateServerLock(server, "_registryGate");
        var lifecycleGate = GetPrivateServerLock(server, "_stateGate");
        Task? stopTask = null;
        Task? updateTask = null;
        registryGate.Enter();
        try
        {
            stopTask = Task.Run(async () =>
                await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false));

            await WaitUntilServerLockHeldAsync(
                () => IsHeldByAnotherThread(lifecycleGate),
                "StopAsync did not acquire the lifecycle gate while waiting for the registry gate");

            updateTask = Task.Run(() =>
                server.UpdateRpcSessionFlushPolicy(2048, TimeSpan.FromMilliseconds(2)));
            await Task.Yield();
            await Task.Yield();

            Ensure(!updateTask.IsCompleted,
                "a runtime flush update must serialize behind StopAsync once stop owns the lifecycle gate");
            Ensure(server.GetRpcSessionFlushPolicySnapshot() == initial,
                "the flush generation must not publish while StopAsync owns the lifecycle boundary");
        }
        finally
        {
            registryGate.Exit();
        }

        var rejected = false;
        try
        {
            await updateTask!.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        Ensure(rejected,
            "the update queued behind the stop owner must be rejected after the lifecycle boundary advances");
        Ensure(server.GetRpcSessionFlushPolicySnapshot() == initial,
            "a stop-rejected flush update must leave the generation unchanged");
        await stopTask!.WaitAsync(TimeSpan.FromSeconds(2));
        await runTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static Lock GetPrivateServerLock(SharpLinkServer server, string propertyName)
        => (Lock)(typeof(SharpLinkServer).GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(server)
            ?? throw new Exception($"cannot find server lock property {propertyName}"));

    private static bool IsHeldByAnotherThread(Lock gate)
    {
        if (!gate.TryEnter())
            return true;
        gate.Exit();
        return false;
    }

    private static async Task WaitUntilServerLockHeldAsync(Func<bool> condition, string failureMessage)
    {
        var deadline = Stopwatch.GetTimestamp() + 2 * Stopwatch.Frequency;
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException(failureMessage);
            await Task.Yield();
        }
    }
}
