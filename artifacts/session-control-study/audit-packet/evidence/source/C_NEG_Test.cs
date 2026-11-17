using System.Reflection;

namespace SharpLink.IntegrationTests;

/// <summary>
/// C-NEG: the intentionally invalid notification-only arm on the unchanged P baseline.
/// The test passes only when it observes the hazard: after the server's Replace commit, while
/// the client manifest broadcast is held, a stale request is accepted by R2.
/// </summary>
public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task C_NEG_StaleRequestAfterReplaceCommitBeforeNotificationHitsR2()
    {
        using var attempt = SessionControlStudyAttempt.Begin("C-NEG-F05");
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("c-neg-f05");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);

        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };

        Task<SharpLinkAssemblyReplacementResult>? replace = null;
        try
        {
            attempt.Emit("before-replace", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["oldCreated"] = plugin.GetStaticInt("Created")
            });

            replace = server.ReplaceAssemblyAsync(
                plugin.ServiceAssembly,
                replacementAssembly,
                TimeSpan.FromSeconds(5)).AsTask();

            // The broadcast worker runs only after the registry commit. Holding its barrier
            // keeps the client on the old manifest while the server already routes by the
            // newly published R2 table.
            await broadcastStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            attempt.Emit("publish-broadcast-held-after-commit", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId
            });

            using var staleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var staleValue = await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 1, staleTimeout.Token);
            var r1Created = plugin.GetStaticInt("Created");
            var r2Created = (int)replacementType.GetProperty("Created")!.GetValue(null)!;
            attempt.Emit("stale-request-observed", new Dictionary<string, object?>
            {
                ["value"] = staleValue,
                ["oldR1Created"] = r1Created,
                ["newR2Created"] = r2Created
            });

            Ensure(staleValue == 2,
                $"P/C-NEG stale request must answer from R2, observed {staleValue}");
            Ensure(r1Created == 0,
                "P/C-NEG stale request must not enter the retired R1 user code");
            Ensure(r2Created == 1,
                "P/C-NEG stale request must enter R2, proving the retarget hazard");

            releaseBroadcast.TrySetResult();
            var replaced = await replace.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                $"P replacement completes: {replaced.Error}");
            attempt.Emit("replacement-completed", new Dictionary<string, object?>
            {
                ["referencesReleased"] = replaced.ReferencesReleased
            });
            attempt.Complete("PASS", "negative-control hazard observed");
        }
        catch (Exception exception)
        {
            attempt.Emit("case-failed", new Dictionary<string, object?>
            {
                ["type"] = exception.GetType().FullName,
                ["message"] = exception.Message
            });
            attempt.Complete("FAIL", exception.Message);
            throw;
        }
        finally
        {
            releaseBroadcast.TrySetResult();
            server.ContractManifestPublishBarrierForTesting = null;
            if (replace is not null)
            {
                try { _ = await replace.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }
}
