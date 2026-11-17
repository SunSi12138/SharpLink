using System.Reflection;
using System.Runtime.CompilerServices;

namespace SharpLink.IntegrationTests;

internal readonly record struct CollectibleRetirement(WeakReference Weak, string OldSessionId);

internal sealed class HeldDuplexStudy
{
    internal IAsyncEnumerator<int> Enumerator = null!;
    internal TaskCompletionSource Gate = null!;
    internal Task<SharpLinkAssemblyReplacementResult> Replace = null!;
    internal WeakReference Weak = null!;
    internal string OldSessionId = string.Empty;
}

/// <summary>
/// Issue #998 G3 core cases for the isolated session-pinned-snapshot prototype (variant S).
/// The same file is compiled against the P baseline where the S-only hooks are absent; the
/// negative-control branch then uses the real manifest broadcast barrier and records the hazard.
/// </summary>
public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task Study_F03_SameContractSameHashReplacementMustChangeSession()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F03");
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        using var plugin = PluginBundle.Load("study-f03");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin, waitForRemoteManifest: false);
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();
        var clientModule = GetDynamicModule(harness.Client, plugin.ContractAssembly);
        var oldContract = clientModule.Manifest.Contracts.Single(candidate =>
            ReferenceEquals(candidate.ContractType, plugin.ContractType));
        var oldHash = clientModule.Manifest.RpcAssemblyHash;

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);
        try
        {
            var before = await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None);
            Ensure(before == 2 && plugin.GetStaticInt("Created") == 1,
                "pre-replacement call enters R1");

            var replaced = await server.ReplaceAssemblyAsync(
                plugin.ServiceAssembly,
                replacementAssembly,
                TimeSpan.FromSeconds(5));
            Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                $"same-hash replacement succeeds: {replaced.Error}");
            attempt.Emit("replacement-committed", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["oldHash"] = oldHash.ToString(),
                ["referencesReleased"] = replaced.ReferencesReleased
            });

            await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                harness.Client, oldSessionId, TimeSpan.FromSeconds(10));
            var newSessionIds = SessionControlStudySupport.ReadySessionIds(harness.Client);
            attempt.Emit("session-replaced", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["newSessionIds"] = newSessionIds
            });

            var fresh = await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 10, CancellationToken.None);
            Ensure(fresh == 11, $"fresh call after session replacement enters R2, observed {fresh}");
            Ensure(plugin.GetStaticInt("Created") == 1,
                "retired R1 does not receive the fresh call");
            Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! == 1,
                "fresh call after session replacement enters R2 exactly once");

            var replacementModule = GetDynamicModule(server, replacementAssembly);
            // The contract assembly is the same object; this is a service-implementation-only
            // replacement. RpcAssemblyHash belongs to the contract owner and is unchanged even
            // when the service module itself reports a different module hash.
            attempt.Emit("fresh-call-completed", new Dictionary<string, object?>
            {
                ["value"] = fresh,
                ["oldR1Created"] = plugin.GetStaticInt("Created"),
                ["newR2Created"] = (int)replacementType.GetProperty("Created")!.GetValue(null)!,
                ["contractId"] = oldContract.ContractId,
                ["contractHash"] = oldHash.ToString(),
                ["serviceModuleHash"] = replacementModule.Manifest.RpcAssemblyHash.ToString()
            });
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
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }

    [Test]
    [NotInParallel]
    public async Task Study_F18b_HeldDuplexMustRetainThenReleaseOldAlc()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F18b");
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        using var plugin = PluginBundle.Load("study-f18b", loadService: false);
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "collectible client contract registers");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "collectible server contract registers");
        var servicePath = Path.Combine(
            Path.GetDirectoryName(plugin.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(servicePath);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);

        var cutCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseNotification = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        releaseNotification.TrySetResult();
        var isS = SessionControlStudySupport.TryInstallSNotificationHooks(
            server: (SharpLinkServer)harness.Server,
            cutCommitted,
            releaseNotification);
        Ensure(isS, "the F18b test requires the S notification hook");

        HeldDuplexStudy? held = null;
        try
        {
            held = await StartHeldDuplexAsync(
                harness, plugin, servicePath, replacementAssembly, cutCommitted);
            Ensure(held.Weak.IsAlive,
                "the held duplex call must keep the old service ALC alive before release");
            Ensure(!held.Replace.IsCompleted,
                "replacement must wait while the old duplex stream is active");
            var sessionsWhileHeld = SessionControlStudySupport.ReadySessionIds(harness.Client);
            Ensure(sessionsWhileHeld.Contains(held.OldSessionId, StringComparer.Ordinal),
                "the old session must remain selectable while the admitted stream is active");
            attempt.Emit("held-duplex-retains-old-session", new Dictionary<string, object?>
            {
                ["oldSessionId"] = held.OldSessionId,
                ["readySessionIds"] = sessionsWhileHeld,
                ["oldAlive"] = held.Weak.IsAlive,
                ["replaceCompleted"] = held.Replace.IsCompleted
            });

            held.Gate.TrySetResult();
            var values = new List<int>();
            while (await held.Enumerator.MoveNextAsync())
                values.Add(held.Enumerator.Current);
            await held.Enumerator.DisposeAsync();
            held.Enumerator = null!;
            attempt.Emit("held-duplex-released", new Dictionary<string, object?>
            {
                ["values"] = values
            });
            Ensure(values.SequenceEqual(new[] { 4 }), "the released duplex stream completes with 4");

            var replaced = await held.Replace.WaitAsync(TimeSpan.FromSeconds(15));
            Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                $"held stream drains and releases the old module: {replaced.Error}");
            await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                harness.Client, held.OldSessionId, TimeSpan.FromSeconds(10));
            held.Replace = null!;
            held.Gate = null!;

            var weak = held.Weak;
            var oldSessionId = held.OldSessionId;
            held = null;
            for (var attemptIndex = 0; attemptIndex < 30 && weak.IsAlive; attemptIndex++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20);
            }
            attempt.Emit("held-duplex-collectible-check", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["oldAlive"] = weak.IsAlive,
                ["readySessionIds"] = SessionControlStudySupport.ReadySessionIds(harness.Client),
                ["retiredServerConnections"] = ServerRegistryTestAccessor.RetiredConnectionCount(
                    (SharpLinkServer)harness.Server)
            });
            Ensure(!weak.IsAlive,
                "old service ALC must be collectible after the held duplex stream exits and the session retires");
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
            try
            {
                held?.Gate.TrySetResult();
                if (held?.Enumerator is { } enumerator)
                    await enumerator.DisposeAsync();
                if (held is not null)
                {
                    try { _ = await held.Replace.WaitAsync(TimeSpan.FromSeconds(5)); }
                    catch (Exception) { }
                }
                _ = await harness.Server.UnregisterAssemblyAsync(
                    replacementAssembly, TimeSpan.FromSeconds(2));
                _ = await harness.Client.UnregisterAssemblyAsync(
                    plugin.ContractAssembly, TimeSpan.FromSeconds(2));
                _ = await harness.Server.UnregisterAssemblyAsync(
                    plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            }
            catch (Exception cleanupException)
            {
                attempt.Emit("cleanup-failed", new Dictionary<string, object?>
                {
                    ["type"] = cleanupException.GetType().FullName,
                    ["message"] = cleanupException.Message
                });
            }
            replacementContext.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<HeldDuplexStudy> StartHeldDuplexAsync(
        DynamicHarness harness,
        PluginBundle plugin,
        string servicePath,
        Assembly replacementAssembly,
        TaskCompletionSource cutCommitted)
    {
        var server = (SharpLinkServer)harness.Server;
        var originalContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var originalAssembly = originalContext.LoadFromAssemblyPath(servicePath);
        Ensure(server.RegisterAssembly(originalAssembly).Succeeded,
            "collectible duplex service registers");
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var enumerator = InvokeStream(
                proxy, plugin.ContractType, "DuplexAsync", GatedValues(gate.Task), CancellationToken.None)
            .GetAsyncEnumerator();
        Ensure(await enumerator.MoveNextAsync(),
            "the duplex stream must yield its first item before the cut");
        Ensure(enumerator.Current == 2, "the first duplex item is 2");

        var replace = server.ReplaceAssemblyAsync(
            originalAssembly,
            replacementAssembly,
            TimeSpan.FromSeconds(20)).AsTask();
        await cutCommitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var weak = new WeakReference(originalContext, trackResurrection: false);
        originalContext.Unload();
        return new HeldDuplexStudy
        {
            Enumerator = enumerator,
            Gate = gate,
            Replace = replace,
            Weak = weak,
            OldSessionId = oldSessionId
        };
    }

    private static async IAsyncEnumerable<int> GatedValues(
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return 1;
        await release.WaitAsync(cancellationToken);
        yield return 2;
    }

    [Test]
    [NotInParallel]
    public async Task Study_F18_RetiredServiceAlcMustNotBeRootedBySessions()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F18");
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        using var plugin = PluginBundle.Load("study-f18", loadService: false);
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "collectible client contract registers");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "collectible server contract registers");
        var servicePath = Path.Combine(
            Path.GetDirectoryName(plugin.ContractAssembly.Location)!,
            "SharpLink.DynamicPlugin.Services.dll");
        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(servicePath);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);
        var server = (SharpLinkServer)harness.Server;
        try
        {
            var retirement = await RetireIdleCollectibleServiceAsync(
                harness, plugin, servicePath, replacementAssembly);
            for (var attemptIndex = 0; attemptIndex < 30 && retirement.Weak.IsAlive; attemptIndex++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(20);
            }

            attempt.Emit("collectible-check", new Dictionary<string, object?>
            {
                ["oldSessionId"] = retirement.OldSessionId,
                ["newSessionIds"] = SessionControlStudySupport.ReadySessionIds(harness.Client),
                ["originalAlive"] = retirement.Weak.IsAlive,
                ["retiredServerConnections"] = ServerRegistryTestAccessor.RetiredConnectionCount(server)
            });
            Ensure(!retirement.Weak.IsAlive,
                "retired service ALC must be collectible after the old session retires; " +
                "the study covers the idle replacement path, not a held streaming enumerator");
            attempt.Complete("PASS", "idle replacement path only; held-stream F18 subcase is NOT_RUN");
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
            try
            {
                _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
                _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
                _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            }
            catch (Exception cleanupException)
            {
                attempt.Emit("cleanup-failed", new Dictionary<string, object?>
                {
                    ["type"] = cleanupException.GetType().FullName,
                    ["message"] = cleanupException.Message
                });
            }
            replacementContext.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<CollectibleRetirement> RetireIdleCollectibleServiceAsync(
        DynamicHarness harness,
        PluginBundle plugin,
        string servicePath,
        Assembly replacementAssembly)
    {
        var server = (SharpLinkServer)harness.Server;
        var originalContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var originalAssembly = originalContext.LoadFromAssemblyPath(servicePath);
        Ensure(server.RegisterAssembly(originalAssembly).Succeeded,
            "collectible original service registers");
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();

        Ensure(await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None) == 2,
            "original collectible service serves a call before retirement");
        var replaced = await server.ReplaceAssemblyAsync(
            originalAssembly,
            replacementAssembly,
            TimeSpan.FromSeconds(5));
        Ensure(replaced.Succeeded && replaced.ReferencesReleased,
            $"collectible service replacement releases old module: {replaced.Error}");
        await SessionControlStudySupport.WaitForReadySessionChangeAsync(
            harness.Client, oldSessionId, TimeSpan.FromSeconds(10));

        var weak = new WeakReference(originalContext, trackResurrection: false);
        originalContext.Unload();
        return new CollectibleRetirement(weak, oldSessionId);
    }

    [Test]
    [NotInParallel]
    public async Task Study_F10_TwoIndependentSessionsMustBothMoveToNewSnapshot()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F10");
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        ISharpLinkClient? secondClient = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
            .UseTcp(IPAddress.Loopback.ToString(), harness.Port)
            .Build();
        using var plugin = PluginBundle.Load("study-f10");
        plugin.ResetServiceState();
        try
        {
            await RegisterAllAsync(harness, plugin, waitForRemoteManifest: false);
            Ensure(secondClient.RegisterAssembly(plugin.ContractAssembly).Succeeded,
                "second client registers the dynamic contract");
            await harness.ConnectClientAsync();
            await secondClient.ConnectAsync();
            await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
            await WaitForRemoteContractManifestAsync(secondClient, plugin.ContractType);

            var server = (SharpLinkServer)harness.Server;
            var proxy1 = GetProxy(harness.Client, plugin.ContractType);
            var proxy2 = GetProxy(secondClient, plugin.ContractType);
            var oldSessions = SessionControlStudySupport.ReadySessionIds(harness.Client)
                .Concat(SessionControlStudySupport.ReadySessionIds(secondClient))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            Ensure(oldSessions.Length == 2, "two independent physical sessions are ready");

            var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
            var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
            var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
            replacementType.GetMethod("Reset")!.Invoke(null, null);
            try
            {
                var replaced = await server.ReplaceAssemblyAsync(
                    plugin.ServiceAssembly,
                    replacementAssembly,
                    TimeSpan.FromSeconds(5));
                Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                    $"replacement succeeds with two sessions: {replaced.Error}");
                await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                    harness.Client, oldSessions[0], TimeSpan.FromSeconds(10));
                await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                    secondClient, oldSessions[1], TimeSpan.FromSeconds(10));
                var newSessions = SessionControlStudySupport.ReadySessionIds(harness.Client)
                    .Concat(SessionControlStudySupport.ReadySessionIds(secondClient))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();
                Ensure(newSessions.Length == 2, "both clients publish a replacement Ready session");
                Ensure(newSessions.Intersect(oldSessions, StringComparer.Ordinal).Any() == false,
                    "S does not reuse an old physical session for either client");
                attempt.Emit("two-sessions-replaced", new Dictionary<string, object?>
                {
                    ["oldSessions"] = oldSessions,
                    ["newSessions"] = newSessions,
                    ["notifications"] = server.PinnedSessionRetirementNotificationCountForTesting
                });

                Ensure(await InvokeValueTaskAsync<int>(
                        proxy1, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None) == 2,
                    "first session executes R2");
                Ensure(await InvokeValueTaskAsync<int>(
                        proxy2, plugin.ContractType, "UnaryAsync", 10, CancellationToken.None) == 11,
                    "second session executes R2");
                Ensure((int)replacementType.GetProperty("Created")!.GetValue(null)! >= 1,
                    "R2 user code is entered after both session replacements");
                attempt.Complete("PASS");
            }
            finally
            {
                _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
                _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
                replacementContext.Unload();
            }
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
            try
            {
                if (secondClient is not null)
                {
                    try { _ = await secondClient.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2)); }
                    catch (Exception) { }
                    await secondClient.DisposeAsync();
                }
                _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
                _ = await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            }
            catch (Exception cleanupException)
            {
                attempt.Emit("cleanup-failed", new Dictionary<string, object?>
                {
                    ["type"] = cleanupException.GetType().FullName,
                    ["message"] = cleanupException.Message
                });
            }
        }
    }

    [Test]
    [NotInParallel]
    public async Task Study_F09_UnrelatedServiceOnSameSessionIsCollateralInS()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F09");
        var control = new ManifestReadinessControl { Operation = _ => ValueTask.FromResult(7) };
        await using var harness = await DynamicHarness.CreateAsync(control: control, connectClient: false);
        using var plugin = PluginBundle.Load("study-f09");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin, waitForRemoteManifest: false);
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var proxy = GetProxy(harness.Client, plugin.ContractType);
        var unrelated = harness.Client.Get<IManifestReadinessControl>();
        var oldSessionId = SessionControlStudySupport.ReadySessionIds(harness.Client).Single();

        var replacementContext = new SharedContractServiceLoadContext(plugin.ContractAssembly);
        var replacementAssembly = replacementContext.LoadFromAssemblyPath(plugin.ServiceAssembly.Location);
        var replacementType = replacementAssembly.GetType("SharpLink.DynamicPlugin.DynamicPluginService", true)!;
        replacementType.GetMethod("Reset")!.Invoke(null, null);
        try
        {
            Ensure(await unrelated.ChangeAsync(1, CancellationToken.None) == 7,
                "unrelated B service works before replacement on the shared session");
            attempt.Emit("unrelated-b-before-cut", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["value"] = 7
            });

            var replaced = await server.ReplaceAssemblyAsync(
                plugin.ServiceAssembly,
                replacementAssembly,
                TimeSpan.FromSeconds(5));
            Ensure(replaced.Succeeded && replaced.ReferencesReleased,
                $"service A replacement succeeds: {replaced.Error}");
            await SessionControlStudySupport.WaitForReadySessionChangeAsync(
                harness.Client, oldSessionId, TimeSpan.FromSeconds(10));
            var newSessionIds = SessionControlStudySupport.ReadySessionIds(harness.Client);
            Ensure(newSessionIds.Length == 1 && newSessionIds[0] != oldSessionId,
                "S replaces the whole physical session that hosted A and B");
            attempt.Emit("unrelated-b-session-collateral", new Dictionary<string, object?>
            {
                ["oldSessionId"] = oldSessionId,
                ["newSessionIds"] = newSessionIds,
                ["foreignServiceInterrupted"] = true,
                ["reason"] = "S retires the entire session, not just A"
            });

            Ensure(await unrelated.ChangeAsync(2, CancellationToken.None) == 7,
                "unrelated B is not misrouted after the session change");
            Ensure(await InvokeValueTaskAsync<int>(
                    proxy, plugin.ContractType, "UnaryAsync", 20, CancellationToken.None) == 21,
                "replacement A remains correct on the new session");
            attempt.Emit("unrelated-b-after-cut", new Dictionary<string, object?>
            {
                ["value"] = 7,
                ["newSessionIds"] = newSessionIds
            });
            attempt.Complete("PASS", "B remains functional but its physical session was replaced");
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
            _ = await server.UnregisterAssemblyAsync(replacementAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.FromSeconds(2));
            _ = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            _ = await server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.FromSeconds(2));
            replacementContext.Unload();
        }
    }

    [Test]
    [NotInParallel]
    public async Task Study_F05_StaleRequestAfterCutBeforeNotificationMustNotEnterR1OrR2()
    {
        using var attempt = SessionControlStudyAttempt.Begin("F05");
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        using var plugin = PluginBundle.Load("study-f05");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin, waitForRemoteManifest: false);
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
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
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        using var plugin = PluginBundle.Load("study-f06");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin, waitForRemoteManifest: false);
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
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
        await using var harness = await DynamicHarness.CreateAsync(connectClient: false);
        using var plugin = PluginBundle.Load("study-f07");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin, waitForRemoteManifest: false);
        await harness.ConnectClientAsync();
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
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
