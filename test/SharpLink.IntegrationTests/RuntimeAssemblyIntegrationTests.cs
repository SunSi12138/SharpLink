using System.Reflection;
using System.Runtime.Loader;

namespace SharpLink.IntegrationTests;

public sealed class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    public async Task RuntimeAssembliesShouldRegisterTransactionallyAndSupportEveryCallShape()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var nullClient = harness.Client.RegisterAssembly(null!);
        var nullServer = harness.Server.RegisterAssembly(null!);
        Ensure(nullClient.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
            "null client registration is a structured error");
        Ensure(nullServer.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.InvalidArgument,
            "null server registration is a structured error");
        var missing = harness.Client.RegisterAssembly(typeof(string).Assembly);
        Ensure(!missing.Succeeded, "assembly without manifest must be rejected");
        Ensure(missing.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingManifest,
            "missing manifest error code");

        using var plugin = PluginBundle.Load("dynamic-call-shapes");
        plugin.ResetServiceState();

        var missingDependency = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
        Ensure(!missingDependency.Succeeded, "service cannot precede its contract");
        Ensure(missingDependency.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.MissingDependency,
            "missing dependency error code");

        var clientContract = harness.Client.RegisterAssembly(plugin.ContractAssembly);
        Ensure(clientContract.Succeeded, $"client contract registration: {clientContract.Error}");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded, "server service registration");

        var duplicate = harness.Server.RegisterAssembly(plugin.ServiceAssembly);
        Ensure(!duplicate.Succeeded, "same Assembly object cannot be registered twice");
        Ensure(duplicate.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly,
            "duplicate assembly error code");
        Ensure(duplicate.Error?.IncomingAssembly?.Contains("SharpLink.DynamicPlugin.Services", StringComparison.Ordinal) == true,
            "duplicate diagnostics contain full assembly identity");
        Ensure(duplicate.Error?.IncomingLoadContext?.Contains("dynamic-call-shapes", StringComparison.Ordinal) == true,
            "duplicate diagnostics contain ALC identity");

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var unary = await InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 7, CancellationToken.None);
        Ensure(unary == 8, "dynamic unary");

        await InvokeValueTaskAsync(proxy, plugin.ContractType, "NotifyAsync", 3, CancellationToken.None);
        await WaitUntilAsync(() => plugin.GetStaticInt("Notifications") == 3);

        var clientStream = await InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "ClientStreamAsync",
            Values(1, 2, 3),
            CancellationToken.None);
        Ensure(clientStream == 6, "dynamic client stream");

        var serverStream = InvokeStream(proxy, plugin.ContractType, "ServerStreamAsync", 3, CancellationToken.None);
        Ensure((await CollectAsync(serverStream)).SequenceEqual([0, 1, 2]), "dynamic server stream");

        var duplex = InvokeStream(
            proxy,
            plugin.ContractType,
            "DuplexAsync",
            Values(2, 4, 6),
            CancellationToken.None);
        Ensure((await CollectAsync(duplex)).SequenceEqual([4, 8, 12]), "dynamic duplex stream");

        try
        {
            _ = await harness.Server.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero);
            throw new Exception("assert failed: contract unload must be blocked by its service dependency");
        }
        catch (InvalidOperationException exception)
        {
            Ensure(exception.Message.Contains("depends on", StringComparison.Ordinal),
                "dependency blocker diagnostic");
        }

        var serviceRelease = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2));
        Ensure(serviceRelease.ReferencesReleased, "service references released");
        Ensure(plugin.GetStaticInt("Disposed") == 1, "dynamic singleton disposed exactly once");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "server contract references released");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract references released");

        try
        {
            _ = await InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None);
            throw new Exception("assert failed: old proxy must fail locally after unregister");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "old proxy draining error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "old proxy draining diagnostic");
        }
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task UnregisterTimeoutShouldKeepRouteOwnedUntilIgnoredCallActuallyEnds()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-draining");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        using var callerCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockIgnoringCancellationAsync",
            callerCancellation.Token).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var timedOut = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(!timedOut.ReferencesReleased, "ignored cancellation keeps framework references");
        Ensure(timedOut.RemainingCalls == 1, "one call remains after drain timeout");

        try
        {
            _ = await InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None);
            throw new Exception("assert failed: draining route must reject new work");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable, "draining server route error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "draining server route diagnostic");
        }

        plugin.ReleaseBlock();
        try
        {
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception exception) when (exception is OperationCanceledException or SharpLinkException)
        {
        }
        await WaitUntilAsync(() => plugin.GetStaticInt("Disposed") == 1);

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "contract releases after background drain");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract releases");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task UnregisterTimeoutShouldCancelCooperativeCallAndNotifyItsClient()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-cooperative-drain");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);

        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockAsync",
            CancellationToken.None).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var released = await harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromMilliseconds(20));
        Ensure(released.ReferencesReleased, "cooperative call releases during targeted cancellation");

        try
        {
            _ = await blocked.WaitAsync(TimeSpan.FromSeconds(2));
            throw new Exception("assert failed: canceled module call must notify its client");
        }
        catch (SharpLinkException exception)
        {
            Ensure(exception.Code == SharpLinkErrorCode.Unavailable,
                "cooperative module cancellation error code");
            Ensure(exception.Message.Contains("module is draining", StringComparison.Ordinal),
                "cooperative module cancellation diagnostic");
        }

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "server contract release after cooperative drain");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract release after cooperative drain");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task SameNamedAssembliesInDifferentCollectibleContextsShouldReportConflictWithoutAliasing()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var first = PluginBundle.Load("same-name-first", loadService: false);
        using var second = PluginBundle.Load("same-name-second", loadService: false);

        var firstRegistration = harness.Client.RegisterAssembly(first.ContractAssembly);
        Ensure(firstRegistration.Succeeded, $"first same-name assembly registers: {firstRegistration.Error}");
        var conflict = harness.Client.RegisterAssembly(second.ContractAssembly);
        Ensure(!conflict.Succeeded, "second same-name route conflicts");
        Ensure(conflict.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.ContractConflict,
            "same-name route conflict code");
        Ensure(conflict.Error?.IncomingLoadContext?.Contains("same-name-second", StringComparison.Ordinal) == true,
            "incoming ALC diagnostic");
        Ensure(conflict.Error?.ExistingLoadContext?.Contains("same-name-first", StringComparison.Ordinal) == true,
            "existing ALC diagnostic");
        Ensure(conflict.Error?.ContractId is not null &&
               conflict.Error?.IncomingFingerprint?.Length == 64 &&
               conflict.Error?.ExistingFingerprint?.Length == 64,
            "route conflict contains ID and full fingerprints");

        Ensure((await harness.Client.UnregisterAssemblyAsync(
            first.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "first same-name assembly releases");
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentUnregisterCallersShouldShareOneDrainOperation()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-shared-unregister");
        plugin.ResetServiceState();
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        var blocked = InvokeValueTaskAsync<int>(
            proxy,
            plugin.ContractType,
            "BlockIgnoringCancellationAsync",
            CancellationToken.None).AsTask();
        await plugin.GetStaticTask("BlockStarted").WaitAsync(TimeSpan.FromSeconds(2));

        var first = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2)).AsTask();
        var second = harness.Server.UnregisterAssemblyAsync(
            plugin.ServiceAssembly,
            TimeSpan.FromSeconds(2)).AsTask();
        Ensure(ReferenceEquals(first, second), "concurrent callers observe the same operation task");
        await Task.Delay(20);
        Ensure(!first.IsCompleted, "shared unregister waits for the active call");

        plugin.ReleaseBlock();
        Ensure(await blocked.WaitAsync(TimeSpan.FromSeconds(2)) == 43, "active call completes during grace");
        var firstResult = await first;
        var secondResult = await second;
        Ensure(firstResult.ReferencesReleased && secondResult.ReferencesReleased,
            "both callers observe successful release");
        Ensure(plugin.GetStaticInt("Disposed") == 1, "shared unregister disposes singleton once");

        Ensure((await harness.Server.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "server contract release");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.FromSeconds(2))).ReferencesReleased, "client contract release");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task ConcurrentRegistrationShouldPublishExactlyOneCompleteSnapshot()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-concurrent-register", loadService: false);
        var registrations = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(
            () => harness.Client.RegisterAssembly(plugin.ContractAssembly))));
        Ensure(registrations.Count(static result => result.Succeeded) == 1,
            "exactly one concurrent registration commits");
        Ensure(registrations.Where(static result => !result.Succeeded).All(static result =>
                result.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.DuplicateAssembly),
            "all losing registrations are structured duplicates");
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(proxy is not null, "published snapshot contains the whole proxy descriptor");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.Zero)).ReferencesReleased, "concurrent registration snapshot releases");
        proxy = null;
    }

    [Test]
    [NotInParallel]
    public async Task CollectibleContextShouldUnloadAfterFrameworkReferencesAreReleased()
    {
        var weakContext = await LoadInvokeUnregisterAndUnloadAsync();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }
        Ensure(!weakContext.IsAlive, "collectible ALC must not be rooted by SharpLink");
    }

    [Test]
    [NotInParallel]
    public async Task TenThousandRegisterUnregisterCyclesShouldLeaveRegistryReusable()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("dynamic-ten-thousand", loadService: false);
        for (var iteration = 0; iteration < 10_000; iteration++)
        {
            var registered = harness.Client.RegisterAssembly(plugin.ContractAssembly);
            Ensure(registered.Succeeded, $"registration cycle {iteration}: {registered.Error}");
            var released = await harness.Client.UnregisterAssemblyAsync(plugin.ContractAssembly, TimeSpan.Zero);
            Ensure(released.ReferencesReleased, $"unregister cycle {iteration}");
        }
        Ensure(harness.Client.RegisterAssembly(plugin.ContractAssembly).Succeeded,
            "registry remains reusable after 10,000 cycles");
        Ensure((await harness.Client.UnregisterAssemblyAsync(
            plugin.ContractAssembly,
            TimeSpan.Zero)).ReferencesReleased, "final cycle releases");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> LoadInvokeUnregisterAndUnloadAsync()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        var plugin = PluginBundle.Load("dynamic-unload");
        RegisterAll(harness, plugin);
        object? proxy = GetProxy(harness.Client, plugin.ContractType);
        Ensure(InvokeProbeBlocking(proxy, plugin.ContractType) == 10, "ALC probe call");
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
        return plugin.Unload();
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

    private static void RegisterAll(DynamicHarness harness, PluginBundle plugin)
    {
        var client = harness.Client.RegisterAssembly(plugin.ContractAssembly);
        Ensure(client.Succeeded, $"client contract registration: {client.Error}");
        Ensure(harness.Server.RegisterAssembly(plugin.ContractAssembly).Succeeded, "server contract registration");
        Ensure(harness.Server.RegisterAssembly(plugin.ServiceAssembly).Succeeded, "server service registration");
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

    private static async Task<int[]> CollectAsync(IAsyncEnumerable<int> values)
    {
        var result = new List<int>();
        await foreach (var value in values.ConfigureAwait(false))
            result.Add(value);
        return [.. result];
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class PluginBundle : IDisposable
    {
        private PluginLoadContext? _context;

        private PluginBundle(
            PluginLoadContext context,
            Assembly contractAssembly,
            Assembly? serviceAssembly,
            Type contractType,
            Type? serviceType)
        {
            _context = context;
            ContractAssembly = contractAssembly;
            ServiceAssembly = serviceAssembly ?? contractAssembly;
            ContractType = contractType;
            ServiceType = serviceType;
        }

        internal Assembly ContractAssembly { get; private set; }
        internal Assembly ServiceAssembly { get; private set; }
        internal Type ContractType { get; private set; }
        private Type? ServiceType { get; set; }

        internal static PluginBundle Load(string contextName, bool loadService = true)
        {
            var directory = GetPluginOutputDirectory();
            var context = new PluginLoadContext(contextName, directory);
            var contract = context.LoadFromAssemblyPath(
                Path.Combine(directory, "SharpLink.DynamicPlugin.Contracts.dll"));
            Assembly? service = null;
            Type? serviceType = null;
            if (loadService)
            {
                service = context.LoadFromAssemblyPath(
                    Path.Combine(directory, "SharpLink.DynamicPlugin.Services.dll"));
                serviceType = service.GetType("SharpLink.DynamicPlugin.DynamicPluginService", throwOnError: true)!;
            }
            return new PluginBundle(
                context,
                contract,
                service,
                contract.GetType("SharpLink.DynamicPlugin.IDynamicPluginService", throwOnError: true)!,
                serviceType);
        }

        internal void ResetServiceState() => InvokeStatic("Reset");

        internal void ReleaseBlock() => InvokeStatic("ReleaseBlock");

        internal int GetStaticInt(string propertyName)
            => (int)(ServiceType!.GetProperty(propertyName)!.GetValue(null) ?? -1);

        internal Task GetStaticTask(string propertyName)
            => (Task)(ServiceType!.GetProperty(propertyName)!.GetValue(null) ??
                throw new InvalidOperationException($"Static task '{propertyName}' was null."));

        private void InvokeStatic(string methodName)
            => ServiceType!.GetMethod(methodName)!.Invoke(null, null);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal WeakReference Unload()
        {
            var context = _context ?? throw new ObjectDisposedException(nameof(PluginBundle));
            var weak = new WeakReference(context, trackResurrection: false);
            ContractAssembly = null!;
            ServiceAssembly = null!;
            ContractType = null!;
            ServiceType = null;
            _context = null;
            context.Unload();
            return weak;
        }

        public void Dispose()
        {
            if (_context is not null)
                _ = Unload();
        }

        private static string GetPluginOutputDirectory()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
                directory = directory.Parent;
            if (directory is null)
                throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
            return Path.Combine(
                directory.FullName,
                "test",
                "SharpLink.DynamicServices",
                "bin",
                "Release",
                "net10.0");
        }
    }

    private sealed class PluginLoadContext(string name, string directory)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            var path = Path.Combine(directory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class DynamicHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;

        private DynamicHarness(
            ISharpLinkServer server,
            ISharpLinkClient client,
            CancellationTokenSource serverCancellation,
            Task serverTask)
        {
            Server = server;
            Client = client;
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
        }

        internal ISharpLinkServer Server { get; }
        internal ISharpLinkClient Client { get; }

        internal static async Task<DynamicHarness> CreateAsync()
        {
            var serverCancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString())
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5));
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = server.RunAsync(serverCancellation.Token).AsTask();
            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .UseHeartbeat(TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(5))
                .Build();
            await client.ConnectAsync();
            return new DynamicHarness(server, client, serverCancellation, serverTask);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.StopAsync();
            await Server.StopAsync(TimeSpan.FromSeconds(2));
            await _serverCancellation.CancelAsync();
            try
            {
                await _serverTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or IOException or SocketException)
            {
            }
            _serverCancellation.Dispose();
        }
    }
}
