using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;

namespace SharpLink.IntegrationTests;

public sealed class Api3BinaryFixtureIntegrationTests
{
    private const string FixtureSha256 =
        "ff123626a634162d89032f97ff617e6cda0f3f5ce287de4b6bb129cbbcf22c9e";

    [Test]
    [NotInParallel]
    public async Task PublishedApi3BinaryShouldExecuteAllCallShapesAndReleaseItsLoadContext()
    {
        var weakContext = await ExecuteFixtureAsync();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Ensure(!weakContext.IsAlive,
            "unregistered API 3 fixture should not leave a collectible load-context root");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> ExecuteFixtureAsync()
    {
        await using var harness = await FixtureHarness.CreateAsync();
        var assemblyBytes = ReadFixtureAssembly();
        var loadContext = new FixtureLoadContext("api3-prebuilt-fixture");
        var weakContext = new WeakReference(loadContext, trackResurrection: false);
        await using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
        var assembly = loadContext.LoadFromStream(assemblyStream);

        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        Ensure(loaded.Succeeded && manifest is not null,
            $"published API 3 fixture manifest should load: {loaded.Error}");
        Ensure(manifest!.ApiVersion == 3 &&
               manifest.ProtocolVersion == SharpLinkGeneratedManifestVersions.Protocol &&
               manifest.GeneratorVersion.StartsWith("1.1.1", StringComparison.Ordinal),
            "fixture should carry the real 1.1.1 Generator API 3 stamp and Protocol 2");
        Ensure(ReferenceEquals(manifest.OwnerAssembly, assembly),
            "fixture manifest should be owned by the prebuilt assembly");

        var contract = manifest.Contracts.Single(descriptor =>
            string.Equals(
                descriptor.ContractType.FullName,
                "SharpLink.Api3Fixture.IApi3FixtureService",
                StringComparison.Ordinal));
        var kinds = contract.Methods.Select(static method => method.Kind).ToHashSet();
        Ensure(kinds.SetEquals([
                RpcMethodKind.Unary,
                RpcMethodKind.OneWay,
                RpcMethodKind.ClientStreaming,
                RpcMethodKind.ServerStreaming,
                RpcMethodKind.DuplexStreaming]),
            "fixture manifest should contain all five generated call shapes");
        Ensure(manifest.Codecs.Any(factory =>
                string.Equals(
                    factory.TargetType.FullName,
                    "SharpLink.Api3Fixture.Api3Payload",
                    StringComparison.Ordinal)),
            "fixture manifest should contain the generated DTO Codec");

        var serverRegistration = harness.Server.RegisterAssembly(assembly);
        var clientRegistration = harness.Client.RegisterAssembly(assembly);
        Ensure(serverRegistration.Succeeded && clientRegistration.Succeeded,
            $"current API 3 Runtime should register the published fixture: " +
            $"server={serverRegistration.Error}, client={clientRegistration.Error}");

        var contractType = contract.ContractType;
        var serviceType = assembly.GetType(
            "SharpLink.Api3Fixture.Api3FixtureService",
            throwOnError: true)!;
        var payloadType = assembly.GetType(
            "SharpLink.Api3Fixture.Api3Payload",
            throwOnError: true)!;
        var proxy = GetProxy(harness.Client, contractType);

        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Value")!.SetValue(payload, 41);
        payloadType.GetProperty("Label")!.SetValue(payload, "fixture");
        var unaryResult = await InvokeResultAsync(
            proxy,
            contractType.GetMethod("UnaryAsync")!,
            payload);
        Ensure((int)payloadType.GetProperty("Value")!.GetValue(unaryResult)! == 42 &&
               string.Equals(
                   (string?)payloadType.GetProperty("Label")!.GetValue(unaryResult),
                   "fixture-api3",
                   StringComparison.Ordinal),
            "API 3 unary call should round-trip through the generated DTO Codec");

        await (ValueTask)(contractType.GetMethod("NotifyAsync")!.Invoke(proxy, [7]) ??
            throw new InvalidOperationException("NotifyAsync returned null."));
        var notificationObserved = (Task)(serviceType.GetProperty("NotificationObserved")!.GetValue(null) ??
            throw new InvalidOperationException("NotificationObserved returned null."));
        await notificationObserved.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure((int)serviceType.GetProperty("Notifications")!.GetValue(null)! == 7,
            "API 3 OneWay call should reach the service exactly once");

        var upload = (ValueTask<int>)(contractType.GetMethod("ClientStreamAsync")!.Invoke(
            proxy,
            [Values(1, 2, 3), CancellationToken.None]) ??
            throw new InvalidOperationException("ClientStreamAsync returned null."));
        Ensure(await upload == 6, "API 3 ClientStreaming should aggregate every item");

        var download = (IAsyncEnumerable<int>)(contractType.GetMethod("ServerStreamAsync")!.Invoke(
            proxy,
            [3, CancellationToken.None]) ??
            throw new InvalidOperationException("ServerStreamAsync returned null."));
        Ensure((await CollectAsync(download)).SequenceEqual([0, 1, 2]),
            "API 3 ServerStreaming should deliver the complete sequence");

        var duplex = (IAsyncEnumerable<int>)(contractType.GetMethod("DuplexAsync")!.Invoke(
            proxy,
            [Values(2, 4, 6), CancellationToken.None]) ??
            throw new InvalidOperationException("DuplexAsync returned null."));
        Ensure((await CollectAsync(duplex)).SequenceEqual([4, 8, 12]),
            "API 3 DuplexStreaming should transform every item");

        var clientDrain = await harness.Client.UnregisterAssemblyAsync(
            assembly,
            TimeSpan.FromSeconds(2));
        var serverDrain = await harness.Server.UnregisterAssemblyAsync(
            assembly,
            TimeSpan.FromSeconds(2));
        Ensure(clientDrain.ReferencesReleased && serverDrain.ReferencesReleased &&
               clientDrain.RemainingCalls == 0 && clientDrain.RemainingStreams == 0 &&
               serverDrain.RemainingCalls == 0 && serverDrain.RemainingStreams == 0,
            "fixture unregister should release all client/server calls, streams, and references");

        proxy = null!;
        payload = null!;
        unaryResult = null;
        contractType = null!;
        serviceType = null!;
        payloadType = null!;
        contract = null!;
        manifest = null;
        assembly = null!;
        loadContext.Unload();
        return weakContext;
    }

    private static byte[] ReadFixtureAssembly()
    {
        var root = FindWorkspaceRoot();
        var encoded = File.ReadAllText(Path.Combine(
            root,
            "test",
            "fixtures",
            "generated-api3",
            "SharpLink.Api3Fixture.dll.gz.b64"));
        var compressed = Convert.FromBase64String(encoded);
        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var assemblyStream = new MemoryStream();
        gzip.CopyTo(assemblyStream);
        var assembly = assemblyStream.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(assembly));
        Ensure(string.Equals(hash, FixtureSha256, StringComparison.Ordinal),
            "prebuilt API 3 fixture checksum should match provenance");
        return assembly;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        return directory?.FullName ??
               throw new DirectoryNotFoundException("SharpLink workspace root was not found.");
    }

    private static object GetProxy(ISharpLinkClient client, Type contractType)
        => typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.Get))!
               .MakeGenericMethod(contractType)
               .Invoke(client, null) ??
           throw new InvalidOperationException("API 3 proxy factory returned null.");

    private static async Task<object?> InvokeResultAsync(
        object target,
        MethodInfo method,
        params object?[] arguments)
    {
        var valueTask = method.Invoke(target, arguments) ??
                        throw new InvalidOperationException($"{method.Name} returned null.");
        var task = (Task)(valueTask.GetType().GetMethod(nameof(ValueTask<int>.AsTask))!.Invoke(
            valueTask,
            null) ?? throw new InvalidOperationException($"{method.Name}.AsTask returned null."));
        await task.WaitAsync(TimeSpan.FromSeconds(2));
        return task.GetType().GetProperty("Result")!.GetValue(task);
    }

    private static async Task<int[]> CollectAsync(IAsyncEnumerable<int> stream)
    {
        var values = new List<int>();
        await foreach (var value in stream)
            values.Add(value);
        return [.. values];
    }

    private static async IAsyncEnumerable<int> Values(params int[] values)
    {
        for (var index = 0; index < values.Length; index++)
        {
            yield return values[index];
            await Task.Yield();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class FixtureLoadContext(string name)
        : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var shared = Default.Assemblies.FirstOrDefault(candidate =>
                AssemblyName.ReferenceMatchesDefinition(candidate.GetName(), assemblyName));
            if (shared is not null)
                return shared;
            var path = Path.Combine(AppContext.BaseDirectory, $"{assemblyName.Name}.dll");
            return File.Exists(path) ? Default.LoadFromAssemblyPath(path) : null;
        }
    }

    private sealed class FixtureHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _serverCancellation;
        private readonly Task _serverTask;

        private FixtureHarness(
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

        internal static async Task<FixtureHarness> CreateAsync()
        {
            var cancellation = new CancellationTokenSource();
            var serverBuilder = SharpLinkServerBuilder.Create()
                .UseTcp(0, IPAddress.Loopback.ToString());
            var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
            var server = serverBuilder.Build();
            var serverTask = server.RunAsync(cancellation.Token).AsTask();
            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .Build();
            await client.ConnectAsync();
            return new FixtureHarness(server, client, cancellation, serverTask);
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
