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
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();
        var initial = server.GetRpcSessionFlushPolicySnapshot();
        var registryGate = GetPrivateServerLock(server, "_registryGate");
        var lifecycleGate = GetPrivateServerLock(server, "_stateGate");
        var registryHeld = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseRegistry = new ManualResetEventSlim(initialState: false);
        var registryOwner = Task.Factory.StartNew(
            () =>
            {
                registryGate.Enter();
                try
                {
                    registryHeld.TrySetResult();
                    releaseRegistry.Wait();
                }
                finally
                {
                    registryGate.Exit();
                }
            },
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        Task? stopTask = null;
        Task? updateTask = null;
        try
        {
            await registryHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));

            stopTask = Task.Run(async () =>
                await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false));
            await WaitUntilServerLockHeldAsync(
                () => IsHeldByAnotherThread(lifecycleGate),
                "StopAsync did not acquire the lifecycle gate while waiting for the registry gate");

            var updateStarted = new TaskCompletionSource(
                TaskCreationOptions.RunContinuationsAsynchronously);
            updateTask = Task.Run(() =>
            {
                updateStarted.TrySetResult();
                server.UpdateRpcSessionFlushPolicy(2048, TimeSpan.FromMilliseconds(2));
            });
            await updateStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Yield();

            Ensure(!updateTask.IsCompleted,
                "a runtime flush update must serialize behind StopAsync once stop owns the lifecycle gate");
            Ensure(server.GetRpcSessionFlushPolicySnapshot() == initial,
                "the flush generation must not publish while StopAsync owns the lifecycle boundary");
        }
        finally
        {
            releaseRegistry.Set();
            await registryOwner.WaitAsync(TimeSpan.FromSeconds(2));
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
