using System.Reflection;

namespace SharpLink.IntegrationTests;

/// <summary>
/// Issue #998 G3 core cases for the isolated session-pinned-snapshot prototype (variant S).
/// The same file is compiled against the P baseline where the S-only hooks are absent; the
/// negative-control branch then uses the real manifest broadcast barrier and records the hazard.
/// </summary>
public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task Study_F05_StaleRequestAfterCutBeforeNotificationMustNotEnterR1OrR2()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F05");
        using var plugin = PluginBundle.Load("study-f05");
        plugin.ResetServiceState();
        await using var harness = await DynamicHarness.CreateAsync();
        await RegisterAllAsync(harness, plugin);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);

        var cutCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isS = SessionControlStudySupport.TryInstallSNotificationHooks(
            server, cutCommitted, releaseNotification);
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

            if (isS)
            {
                await cutCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                attempt.Emit("server-cut-committed", new Dictionary<string, object?>
                {
                    ["oldSessionId"] = oldSessionId,
                    ["notificationHeld"] = true
                });
            }
            else
            {
                // P has no cut hook. The manifest publish worker runs only after the registry
                // commit; holding its barrier proves the commit happened while the client still
                // has the old manifest.
                await broadcastStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                attempt.Emit("publish-broadcast-held-after-commit", new Dictionary<string, object?>
                {
                    ["oldSessionId"] = oldSessionId
                });
            }

            using var staleTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var staleOutcome = "response";
            var staleValue = 0;
            try
            {
                staleValue = await InvokeValueTaskAsync<int>(
                    proxy, plugin.ContractType, "UnaryAsync", 1, staleTimeout.Token);
            }
            catch (SharpLinkException exception)
            {
                staleOutcome = exception.Code.ToString();
                attempt.Emit("stale-request-rejected", new Dictionary<string, object?>
                {
                    ["code"] = exception.Code.ToString(),
                    ["detail"] = exception.DetailCode
                });
            }

            attempt.Emit("stale-request-observed", new Dictionary<string, object?>
            {
                ["outcome"] = staleOutcome,
                ["value"] = staleValue,
                ["oldR1Created"] = plugin.GetStaticInt("Created"),
                ["newR2Created"] = (int)replacementType.GetProperty("Created")!.GetValue(null)!
            });

            if (isS)
            {
                Ensure(staleOutcome == "Unavailable",
                    $"S must reject the stale request with Unavailable, observed {staleOutcome}");
                Ensure(plugin.GetStaticInt("Created") == 0,
                    "S: stale request must not enter R1 user code");
                Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! == 0,
                    "S: stale request must not enter R2 user code");
            }
            else
            {
                // Deliberate negative control: P resolves the live registry after the Replace
                // commit and therefore retargets the stale request to R2 while the client still
                // holds the old manifest. Capturing this is the whole point of C-NEG.
                Ensure(staleOutcome == "response" && staleValue == 2,
                    $"P/C-NEG must demonstrate the stale-request retarget hazard, observed {staleOutcome}/{staleValue}");
                Ensure(plugin.GetStaticInt("Created") == 0,
                    "P/C-NEG: stale request must not enter the retired R1 user code");
                Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! == 1,
                    "P/C-NEG: stale request must have entered R2, proving retargeting");
            }

            releaseNotification.TrySetResult();
            releaseBroadcast.TrySetResult();
            var replaced = await replace.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(replaced.Succeeded, $"replacement publishes: {replaced.Error}");
            Ensure(replaced.ReferencesReleased, "replacement physically releases old module references");
            attempt.Emit("replacement-completed", new Dictionary<string, object?>
            {
                ["succeeded"] = replaced.Succeeded,
                ["referencesReleased"] = replaced.ReferencesReleased,
                ["remainingCalls"] = replaced.RemainingCalls
            });

            if (isS)
            {
                await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                    harness.Client, oldSessionId, TimeSpan.FromSeconds(10));
                attempt.Emit("replacement-session-ready", new Dictionary<string, object?>
                {
                    ["oldSessionId"] = oldSessionId,
                    ["readySessionIds"] = SessionControlStudySupport.ReadySessionIds(harness.Client)
                });
            }

            using var freshTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var fresh = await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 10, freshTimeout.Token);
            Ensure(fresh == 11, $"fresh request after cut executes R2, observed {fresh}");
            Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! >= 1,
                "fresh request after cut enters R2 user code");
            attempt.Emit("fresh-request-completed", new Dictionary<string, object?> { ["value"] = fresh });
            attempt.Complete("PASS");
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
            releaseNotification.TrySetResult();
            releaseBroadcast.TrySetResult();
            server.ContractManifestPublishBarrierForTesting = null;
            SessionControlStudySupport.ClearSNotificationHooks(server);
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

    [Test]
    [NotInParallel]
    public async Task Study_F06_AdmittedOldCallMustCompleteAfterCut()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F06");
        using var plugin = PluginBundle.Load("study-f06");
        plugin.ResetServiceState();
        await using var harness = await DynamicHarness.CreateAsync();
        await RegisterAllAsync(harness, plugin);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);

        var cutCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isS = SessionControlStudySupport.TryInstallSNotificationHooks(
            server, cutCommitted, releaseNotification);
        server.ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };

        Task<SharpLinkAssemblyReplacementResult>? replace = null;
        var call = InvokeValueTaskAsync<int>(
            proxy, plugin.ContractType, "BlockAsync", CancellationToken.None);
        try
        {
            await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(5));
            attempt.Emit("old-call-entered-business", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["blockStarted"] = true
            });

            replace = server.ReplaceAssemblyAsync(
                plugin.ServiceAssembly,
                replacementAssembly,
                TimeSpan.FromSeconds(10)).AsTask();
            if (isS)
                await cutCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await broadcastStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            attempt.Emit("cut-observed-with-active-old-call", new Dictionary<string, object?>
            {
                ["callCompleted"] = call.IsCompleted,
                ["oldSessionId"] = oldSessionId
            });
            Ensure(!call.IsCompleted, "the admitted old call must remain active across the cut");

            plugin.ReleaseBlock();
            var value = await call.AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(value == 42, $"admitted old call completes with its original R1 result, observed {value}");
            attempt.Emit("old-call-completed", new Dictionary<string, object?>
            {
                ["value"] = value,
                ["oldCreated"] = plugin.GetStaticInt("Created")
            });

            releaseNotification.TrySetResult();
            releaseBroadcast.TrySetResult();
            var replaced = await replace.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                $"replacement completes after old call: {replaced.Error}");

            if (isS)
            {
                await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                    harness.Client, oldSessionId, TimeSpan.FromSeconds(10));
            }

            attempt.Complete("PASS");
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
            plugin.ReleaseBlock();
            releaseNotification.TrySetResult();
            releaseBroadcast.TrySetResult();
            server.ContractManifestPublishBarrierForTesting = null;
            SessionControlStudySupport.ClearSNotificationHooks(server);
            try { _ = await call.AsTask().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
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

    [Test]
    [NotInParallel]
    public async Task Study_F07_QueuedRequestLosingToCutMustEnterNeitherR1NorR2()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F07");
        using var plugin = PluginBundle.Load("study-f07");
        plugin.ResetServiceState();
        await using var harness = await DynamicHarness.CreateAsync();
        await RegisterAllAsync(harness, plugin);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();
        var holder = await HoldQueuedAdmissionAsync(server);
        var admission = server.CurrentAdmissionProgramForTests!;

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);

        var cutCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var broadcastStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBroadcast = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var isS = SessionControlStudySupport.TryInstallSNotificationHooks(
            server, cutCommitted, releaseNotification);
        server.ContractManifestPublishBarrierForTesting = () =>
        {
            broadcastStarted.TrySetResult();
            return releaseBroadcast.Task;
        };

        Task<SharpLinkAssemblyReplacementResult>? replace = null;
        Task<int>? call = null;
        try
        {
            call = InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None).AsTask();
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 1);
            attempt.Emit("request-queued", new Dictionary<string, object?>
            {
                ["queuedCalls"] = admission.Kernel.QueuedCalls,
                ["activePermits"] = admission.Kernel.ActivePermits,
                ["oldSessionId"] = oldSessionId
            });

            replace = server.ReplaceAssemblyAsync(
                plugin.ServiceAssembly,
                replacementAssembly,
                TimeSpan.FromSeconds(10)).AsTask();
            if (isS)
                await cutCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            else
                await broadcastStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            attempt.Emit("cut-before-permit-release", new Dictionary<string, object?>
            {
                ["queuedCalls"] = admission.Kernel.QueuedCalls,
                ["oldCreated"] = plugin.GetStaticInt("Created"),
                ["newCreated"] = (int)replacementType.GetProperty("Created")!.GetValue(null)!
            });

            holder.Lease!.Dispose();
            var outcome = "response";
            try
            {
                var value = await call.WaitAsync(TimeSpan.FromSeconds(5));
                attempt.Emit("queued-request-response", new Dictionary<string, object?> { ["value"] = value });
            }
            catch (SharpLinkException exception)
            {
                outcome = exception.Code.ToString();
                attempt.Emit("queued-request-rejected", new Dictionary<string, object?>
                {
                    ["code"] = exception.Code.ToString(),
                    ["detail"] = exception.DetailCode
                });
            }

            Ensure(outcome == "Unavailable",
                $"a queued request that loses the cut must reject with Unavailable, observed {outcome}");
            Ensure(plugin.GetStaticInt("Created") == 0,
                "queued request must not enter retired R1 user code");
            Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! == 0,
                "queued request must not be retargeted into R2 user code");
            await WaitUntilAsync(() => admission.Kernel.QueuedCalls == 0 && admission.Kernel.ActivePermits == 0);

            releaseNotification.TrySetResult();
            releaseBroadcast.TrySetResult();
            var replaced = await replace.WaitAsync(TimeSpan.FromSeconds(10));
            Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                $"replacement completes after queued rejection: {replaced.Error}");
            if (isS)
            {
                await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                    harness.Client, oldSessionId, TimeSpan.FromSeconds(10));
            }
            attempt.Complete("PASS");
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
            holder.Lease!.Dispose();
            releaseNotification.TrySetResult();
            releaseBroadcast.TrySetResult();
            server.ContractManifestPublishBarrierForTesting = null;
            SessionControlStudySupport.ClearSNotificationHooks(server);
            if (call is not null)
            {
                try { _ = await call.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
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
