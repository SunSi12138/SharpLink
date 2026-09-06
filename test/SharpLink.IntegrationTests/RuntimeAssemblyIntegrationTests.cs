using System.Reflection;
using System.Runtime.Loader;
using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{


































    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<TrackedWeakReferences> LoadInvokeUnregisterAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-unload");
        await RegisterAllAsync(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(InvokeProbeBlocking(proxy, plugin.ContractType) == 10, "ALC probe call");
        var payloadType = plugin.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Value")!.SetValue(payload, 5);
        payloadType.GetProperty("Label")!.SetValue(payload, "alc-sharppack");
        payloadType.GetProperty("Parent")!.SetValue(payload, payload);
        ((System.Collections.IList)payloadType.GetProperty("Values")!.GetValue(payload)!).Add(7);
        Ensure(await InvokeValueTaskAsync<int>(
                proxy, plugin.ContractType, "UsePayloadAsync", payload, CancellationToken.None) == 25,
            "ALC SharpPack payload call");
        var tracked = CapturePluginRuntimeObjects(harness.Client, plugin.ContractAssembly);
        tracked.Add("ContractAssembly", plugin.ContractAssembly);
        tracked.Add("ServiceAssembly", plugin.ServiceAssembly);
        tracked.Add("ContractType", plugin.ContractType);
        tracked.Add("PayloadType", payloadType);
        payload = null;
        payloadType = null!;
        proxy = null;
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "ALC service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "ALC server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "ALC client contract release");
        tracked.Add("AssemblyLoadContext", plugin.Unload());
        return tracked;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExecuteDynamicStreamExitAndUnloadAsync(string exitMode)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load($"api4-stream-exit-{exitMode}");
        await RegisterAllAsync(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);

        if (string.Equals(exitMode, "normal", StringComparison.Ordinal))
        {
            Ensure((await CollectAsync(InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ServerStreamAsync",
                    3,
                    CancellationToken.None))).SequenceEqual([0, 1, 2]),
                "normal API 4 dynamic stream completes");
        }
        else if (string.Equals(exitMode, "service-exception", StringComparison.Ordinal))
        {
            try
            {
                _ = await CollectAsync(InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ThrowingServerStreamAsync",
                    CancellationToken.None));
                throw new Exception("assert failed: dynamic service stream must fail");
            }
            catch (SharpLinkException exception)
            {
                Ensure(exception.Code == SharpLinkErrorCode.Internal,
                    "dynamic service stream exception maps to Internal");
            }
        }
        else if (string.Equals(exitMode, "cancellation-before-first", StringComparison.Ordinal))
        {
            using var cancellation = new CancellationTokenSource();
            await using var enumerator = InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ServerStreamAsync",
                    int.MaxValue,
                    cancellation.Token)
                .GetAsyncEnumerator();
            cancellation.Cancel();
            var cancelled = false;
            try
            {
                _ = await enumerator.MoveNextAsync();
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            Ensure(cancelled,
                "API 4 dynamic stream cancellation before the first item reaches the caller");
        }
        else
        {
            using var cancellation = new CancellationTokenSource();
            var token = string.Equals(exitMode, "cancellation-mid-stream", StringComparison.Ordinal)
                ? cancellation.Token
                : CancellationToken.None;
            await using var enumerator = InvokeStream(
                    proxy,
                    plugin.ContractType,
                    "ServerStreamAsync",
                    int.MaxValue,
                    token)
                .GetAsyncEnumerator();
            Ensure(await enumerator.MoveNextAsync(),
                $"API 4 dynamic stream '{exitMode}' starts before exit");
            if (string.Equals(exitMode, "cancellation-mid-stream", StringComparison.Ordinal))
            {
                cancellation.Cancel();
                var cancelled = false;
                try
                {
                    _ = await enumerator.MoveNextAsync();
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                }
                Ensure(cancelled,
                    "API 4 dynamic stream cancellation after the first item reaches the caller");
            }
            else
                Ensure(string.Equals(exitMode, "consumer-break", StringComparison.Ordinal),
                    $"unknown dynamic stream exit mode '{exitMode}'");
        }

        if (!string.Equals(exitMode, "cancellation-before-first", StringComparison.Ordinal))
        {
            await plugin.GetStaticTask("ServerStreamDisposed").WaitAsync(TimeSpan.FromSeconds(2));
        }
        proxy = null;
        var service = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(service.ReferencesReleased,
            $"API 4 dynamic stream '{exitMode}' releases its service module before dependants");
        var serverContract = await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        var clientContract = await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serverContract.ReferencesReleased && clientContract.ReferencesReleased,
            $"API 4 dynamic stream '{exitMode}' releases all module references");
        EnsureClientAndServerCountersAreZero(harness, $"API 4 dynamic stream '{exitMode}'");
        return plugin.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RejectConflictingApi4AssemblyAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var accepted = PluginBundle.Load("api4-registration-accepted", loadService: false);
        var rejected = PluginBundle.Load("api4-registration-rejected", loadService: false);
        Ensure(harness.Client.RegisterAssembly(accepted.ContractAssembly).Succeeded,
            "first API 4 dynamic contract registers");
        var conflict = harness.Client.RegisterAssembly(rejected.ContractAssembly);
        Ensure(!conflict.Succeeded &&
               conflict.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            "conflicting API 4 dynamic contract is rejected before publication");
        var weakContext = rejected.Unload();
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            accepted.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased,
            "accepted API 4 contract releases after conflict verification");
        return weakContext;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RegisterRemoveAndUnloadMultiClusterPluginAsync()
    {
        var plugin = PluginBundle.Load("multi-cluster-runtime-remove", loadService: false);
        await using var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster(
                "plugins",
                child => child.UseTcp(IPAddress.Loopback.ToString(), 1),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        Ensure(client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded,
            "runtime-remove setup registration");
        object? proxy = GetMultiClusterProxy(client, plugin.ContractType);
        Ensure(proxy is not null, "runtime-remove setup proxy");

        var removal = await client.RemoveClusterAsync("plugins", TimeSpan.FromSeconds(2));
        Ensure(removal is { Succeeded: true, ReferencesReleased: true, ForcedStop: false },
            "runtime-remove child cleanup must complete before unloading the plugin context");
        proxy = null;
        return plugin.Unload();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static TrackedWeakReferences CapturePluginRuntimeObjects(
        ISharpLinkClient client,
        Assembly contractAssembly)
    {
        const BindingFlags instanceFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var tracked = new TrackedWeakReferences();
        var runtimeContext = (IRpcRuntimeContext)client.GetType()
            .GetProperty("RuntimeContext", instanceFlags)!
            .GetValue(client)!;
        var registrations = runtimeContext.GetType()
            .GetField("_manifestRegistrations", instanceFlags)!
            .GetValue(runtimeContext) as System.Collections.IEnumerable
            ?? throw new InvalidOperationException("Runtime Manifest registrations were not enumerable.");
        foreach (var registration in registrations)
        {
            var registrationType = registration!.GetType();
            var manifest = (ISharpLinkGeneratedAssemblyManifest)registrationType
                .GetProperty("Manifest", instanceFlags)!
                .GetValue(registration)!;
            if (!ReferenceEquals(manifest.OwnerAssembly, contractAssembly))
                continue;

            tracked.Add("Manifest", manifest);
            tracked.Add("ManifestType", manifest.GetType());
            var scopes = (Array)registrationType.GetField("_scopes", instanceFlags)!.GetValue(registration)!;
            foreach (var scope in scopes)
            {
                tracked.Add("AdapterScope", scope!);
                var serializerContext = scope!.GetType().GetField("_context", instanceFlags)!.GetValue(scope);
                if (serializerContext is not null)
                    tracked.Add("SharpPackSerializerContext", serializerContext);
            }

            var codecs = registrationType.GetProperty("Codecs", instanceFlags)!.GetValue(registration)
                as System.Collections.IEnumerable
                ?? throw new InvalidOperationException("Manifest Codec registrations were not enumerable.");
            foreach (var pair in codecs)
            {
                var pairType = pair!.GetType();
                tracked.Add("CodecTargetType", pairType.GetProperty("Key")!.GetValue(pair)!);
                var codecRegistration = pairType.GetProperty("Value")!.GetValue(pair)!;
                var codecRegistrationType = codecRegistration.GetType();
                tracked.Add("GeneratedCodecFactory",
                    codecRegistrationType.GetProperty("Factory", instanceFlags)!.GetValue(codecRegistration)!);
                var preparedCodec = codecRegistrationType
                    .GetField("_preparedCodec", instanceFlags)!
                    .GetValue(codecRegistration);
                if (preparedCodec is not null)
                    tracked.Add("PreparedCodec", preparedCodec);
            }
        }
        Ensure(tracked.Count >= 7, "plugin runtime Adapter state was captured before unregister");
        return tracked;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int InvokeProbeBlocking(object proxy, Type contractType)
        => InvokeValueTaskAsync<int>(
                proxy,
                contractType,
                "UnaryAsync",
                9,
                CancellationToken.None)
            .AsTask()
            .GetAwaiter()
            .GetResult();

    private static async Task RegisterAllAsync(DynamicHarness harness, PluginBundle plugin)
    {
        var client = harness.Client.RegisterAssembly(plugin.ContractAssembly);
        Ensure(client.Succeeded, $"client contract registration: {client.Error}");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded, "server service registration");
        await WaitForRemoteContractManifestAsync(harness.Client, plugin.ContractType);
    }

    private static void EnsureReplacementReleased(
        SharpLinkAssemblyReplacementResult result,
        string name)
        => Ensure(result.Succeeded &&
                  result.ReferencesReleased &&
                  result.RemainingCalls == 0 &&
                  result.RemainingStreams == 0,
            $"{name} publishes atomically and releases every old module counter: {result.Error}");

    private static void EnsureUnregisterReleased(
        SharpLinkAssemblyUnregisterResult result,
        string name)
        => Ensure(result.ReferencesReleased &&
                  result.RemainingCalls == 0 &&
                  result.RemainingStreams == 0,
            $"{name} releases every module counter");

    private static void EnsureClientAndServerCountersAreZero(
        DynamicHarness harness,
        string name)
    {
        var client = (SharpLinkClient)harness.Client;
        var serverActiveCalls =
            ServerCallAdmissionDiagnostics.ActiveCallCount(harness.Server);
        Ensure(client.PendingCallCount == 0 &&
               client.ActiveClientCallCount == 0 &&
               client.ActiveClientStreamCount == 0 &&
               serverActiveCalls == 0,
            $"{name} leaves client pending/call/stream and server call counters at zero");
    }

    private static SharpLinkDynamicModule GetDynamicModule(object owner, Assembly assembly)
    {
        var modules = owner.GetType().GetField(
                "_dynamicModules",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
            as System.Collections.IDictionary
            ?? throw new InvalidOperationException("Dynamic module registry was not found.");
        return modules[assembly] as SharpLinkDynamicModule
            ?? throw new InvalidOperationException(
                $"Dynamic module was not found for '{assembly.FullName}'.");
    }

    private static async Task WaitForRemoteContractManifestAsync(
        ISharpLinkClient client,
        Type contractType)
    {
        var module = GetDynamicModule(client, contractType.Assembly);
        var contract = module.Manifest.Contracts.Single(candidate =>
            ReferenceEquals(candidate.ContractType, contractType));
        var expectedHash = module.Manifest.RpcAssemblyHash;
        var snapshotField = typeof(SharpLinkClient).GetField(
            "_remoteContractManifestSnapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Remote contract manifest snapshot field was not found.");
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3d);

        while (true)
        {
            var bindings = snapshotField.GetValue(client) as Array
                ?? throw new InvalidOperationException("Remote contract manifest snapshot was unavailable.");
            for (var index = 0; index < bindings.Length; index++)
            {
                var binding = bindings.GetValue(index)!;
                var manifestProperty = binding.GetType().GetProperty(
                    "Manifest",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Remote contract manifest binding was malformed.");
                var manifest = (ProtocolV2ContractManifest)manifestProperty.GetValue(binding)!;
                if (manifest.Contracts.TryGetValue(contract.ContractId, out var remoteHash) &&
                    remoteHash == expectedHash)
                {
                    return;
                }
            }

            if (Stopwatch.GetTimestamp() >= deadline)
            {
                throw new TimeoutException(
                    $"Remote contract manifest did not publish '{contract.ContractName}' " +
                    $"({contract.ContractId}) with RpcAssemblyHash '{expectedHash}'.");
            }
            await Task.Delay(10);
        }
    }

    private static bool HasLocalProxyDescriptor(ISharpLinkClient client, Type contractType)
    {
        var snapshotField = typeof(SharpLinkClient).GetField(
            "_proxies",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Client proxy snapshot field was not found.");
        var snapshot = snapshotField.GetValue(client)
            ?? throw new InvalidOperationException("Client proxy snapshot was unavailable.");
        var containsKey = snapshot.GetType().GetMethod("ContainsKey", [typeof(Type)])
            ?? throw new InvalidOperationException("Client proxy snapshot lookup method was not found.");
        return (bool)containsKey.Invoke(snapshot, [contractType])!;
    }

    private static object GetProxy(ISharpLinkClient client, Type contractType)
    {
        var get = typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.Get))!
            .MakeGenericMethod(contractType);
        return get.Invoke(client, null) ?? throw new InvalidOperationException("Dynamic proxy factory returned null.");
    }

    private static ValueTask<T> InvokeValueTaskAsync<T>(
        object proxy,
        Type contractType,
        string methodName,
        params object?[] arguments)
        => (ValueTask<T>)(contractType.GetMethod(methodName)!.Invoke(proxy, arguments) ??
            throw new InvalidOperationException($"{methodName} returned null."));

    private static ValueTask InvokeValueTaskAsync(
        object proxy,
        Type contractType,
        string methodName,
        params object?[] arguments)
        => (ValueTask)(contractType.GetMethod(methodName)!.Invoke(proxy, arguments) ??
            throw new InvalidOperationException($"{methodName} returned null."));

    private static IAsyncEnumerable<int> InvokeStream(
        object proxy,
        Type contractType,
        string methodName,
        params object?[] arguments)
        => (IAsyncEnumerable<int>)(contractType.GetMethod(methodName)!.Invoke(proxy, arguments) ??
            throw new InvalidOperationException($"{methodName} returned null."));

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            yield return values[index];
            await Task.Yield();
        }
    }

    private static async IAsyncEnumerable<int> BlockingValues(
        TaskCompletionSource started,
        Task release,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;
        started.TrySetResult();
        await release.ConfigureAwait(false);
        yield return 1;
    }

    private static async Task<int[]> CollectAsync(IAsyncEnumerable<int> values)
    {
        var result = new List<int>();
        await foreach (var value in values.ConfigureAwait(false))
            result.Add(value);
        return [.. result];
    }

    private static async Task EnsureDrainingStreamAsync(IAsyncEnumerable<int> stream, string name)
    {
        try
        {
            _ = await CollectAsync(stream);
            throw new Exception($"assert failed: {name} must fail after module release");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, $"{name} draining error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                $"{name} draining diagnostic");
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3d);
        while (!condition())
        {
            if (Stopwatch.GetTimestamp() >= deadline)
                throw new TimeoutException("Condition was not reached within three seconds.");
            await Task.Delay(10);
        }
    }

    private static async Task<ISharpLinkMultiClusterClient> CreateDynamicMultiClusterClientAsync(int port)
    {
        var client = SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster("plugins", child => child.UseTcp(IPAddress.Loopback.ToString(), port),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync();
        return client;
    }

    private static async Task RegisterMultiClusterPluginAsync(
        DynamicHarness harness,
        ISharpLinkMultiClusterClient client,
        PluginBundle plugin)
    {
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "multi-cluster server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded,
            "multi-cluster server service registration");
        Ensure(client.RegisterAssembly("plugins", plugin.ContractAssembly).Succeeded,
            "multi-cluster client contract registration");

        var snapshotField = typeof(SharpLinkMultiClusterClient).GetField(
            "_snapshot",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Multi-cluster snapshot field was not found.");
        var snapshot = (MultiClusterSnapshot)(snapshotField.GetValue(client)
            ?? throw new InvalidOperationException("Multi-cluster snapshot was unavailable."));
        await WaitForRemoteContractManifestAsync(
            snapshot.Clusters[new SharpLinkClusterKey("plugins")].Client,
            plugin.ContractType);
    }

    private static async Task UnregisterMultiClusterPluginAsync(DynamicHarness harness, PluginBundle plugin)
    {
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "multi-cluster server service release");
        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly, TimeSpan.FromSeconds(2))).ReferencesReleased,
            "multi-cluster server contract release");
    }

    private static async Task EnsureCancelledAsync(Task task, string name)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new Exception($"assert failed: {name} should observe caller cancellation");
    }

    private static async Task<SharpLinkAssemblyUnregisterResult> UnregisterWhenReplacementPublishesAsync(
        ISharpLinkMultiClusterClient client,
        Assembly assembly)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * 3d);
        while (true)
        {
            try
            {
                return await client.UnregisterAssemblyAsync("plugins", assembly, TimeSpan.FromSeconds(2));
            }
            catch (InvalidOperationException) when (Stopwatch.GetTimestamp() < deadline)
            {
                await Task.Delay(10);
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (ContainsMessage(inner, message))
                    return true;
            return false;
        }
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

}
