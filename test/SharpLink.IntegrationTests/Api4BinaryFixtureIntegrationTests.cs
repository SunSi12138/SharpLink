using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.IntegrationTests;

public sealed class Api4BinaryFixtureIntegrationTests
{
    private const string FixtureSha256 =
        "5a6adda8bef11941e1175505f090ebf8db304f268bfa157ba4021e603b180d61";

    [Test]
    [NotInParallel]
    public async Task FrozenApi4BinaryShouldBeRejectedBeforePublicationAndReleaseItsLoadContext()
    {
        var weakContext = await RejectFixtureAsync();
        for (var attempt = 0; attempt < 20 && weakContext.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            await Task.Delay(20);
        }

        Ensure(!weakContext.IsAlive,
            "rejected API 4 fixture should not leave a collectible load-context root");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference> RejectFixtureAsync()
    {
        await using var harness = await FixtureHarness.CreateAsync();
        var clientModulesBefore = GetSnapshotCount(harness.Client, "_dynamicModules");
        var clientProxiesBefore = GetSnapshotCount(harness.Client, "_proxies");
        var clientCodecsBefore = GetGeneratedCodecCount(harness.Client);
        var serverModulesBefore = GetSnapshotCount(harness.Server, "_dynamicModules");
        var serverServicesBefore = GetSnapshotCount(harness.Server, "_services");
        var serverCodecsBefore = GetGeneratedCodecCount(harness.Server);
        var multiRegistrationsBefore = GetSnapshotCount(harness.MultiClient, "_dynamicRegistrations");
        var assemblyBytes = ReadFixtureAssembly();
        var loadContext = new FixtureLoadContext("api4-prebuilt-fixture");
        var weakContext = new WeakReference(loadContext, trackResurrection: false);
        await using var assemblyStream = new MemoryStream(assemblyBytes, writable: false);
        var assembly = loadContext.LoadFromStream(assemblyStream);

        var loaded = SharpLinkAssemblyManifestLoader.TryLoad(assembly, out var manifest);
        Ensure(!loaded.Succeeded && manifest is null,
            "the API 5 Runtime must reject the frozen API 4 fixture");

        var serverRegistration = harness.Server.RegisterAssembly(assembly);
        var clientRegistration = harness.Client.RegisterAssembly(assembly);
        var multiRegistration = harness.MultiClient.RegisterAssembly("plugins", assembly);
        var clientReplacement = await harness.Client.ReplaceAssemblyAsync(
            typeof(Api4BinaryFixtureIntegrationTests).Assembly,
            assembly,
            TimeSpan.Zero);
        var serverReplacement = await harness.Server.ReplaceAssemblyAsync(
            typeof(Api4BinaryFixtureIntegrationTests).Assembly,
            assembly,
            TimeSpan.Zero);
        var multiReplacement = await harness.MultiClient.ReplaceAssemblyAsync(
            "plugins",
            typeof(Api4BinaryFixtureIntegrationTests).Assembly,
            assembly,
            TimeSpan.Zero);
        AssertApi4Rejection(loaded.Error, assembly, "direct loader");
        AssertApi4Rejection(clientRegistration.Error, assembly, "Client registration");
        AssertApi4Rejection(serverRegistration.Error, assembly, "Server registration");
        AssertApi4Rejection(multiRegistration.Error, assembly, "multi-cluster registration");
        AssertApi4Rejection(clientReplacement.Error, assembly, "Client replacement");
        AssertApi4Rejection(serverReplacement.Error, assembly, "Server replacement");
        AssertApi4Rejection(multiReplacement.Error, assembly, "multi-cluster replacement");
        Ensure(GetSnapshotCount(harness.Client, "_dynamicModules") == clientModulesBefore &&
               GetSnapshotCount(harness.Client, "_proxies") == clientProxiesBefore &&
               GetGeneratedCodecCount(harness.Client) == clientCodecsBefore,
            "client rejection must publish no module, proxy, or Codec");
        Ensure(GetSnapshotCount(harness.Server, "_dynamicModules") == serverModulesBefore &&
               GetSnapshotCount(harness.Server, "_services") == serverServicesBefore &&
               GetGeneratedCodecCount(harness.Server) == serverCodecsBefore,
            "server rejection must publish no module, service, or Codec");
        Ensure(GetSnapshotCount(harness.MultiClient, "_dynamicRegistrations") == multiRegistrationsBefore,
            "multi-cluster rejection must publish no dynamic registration");

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
            "generated-api4",
            "SharpLink.Api4Fixture.dll.gz.b64"));
        var compressed = Convert.FromBase64String(encoded);
        using var compressedStream = new MemoryStream(compressed, writable: false);
        using var gzip = new GZipStream(compressedStream, CompressionMode.Decompress);
        using var assemblyStream = new MemoryStream();
        gzip.CopyTo(assemblyStream);
        var assembly = assemblyStream.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(assembly));
        Ensure(string.Equals(hash, FixtureSha256, StringComparison.Ordinal),
            "prebuilt API 4 fixture checksum should match provenance");
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

    private static int GetSnapshotCount(object owner, string fieldName)
    {
        var field = owner.GetType().GetField(
            fieldName,
            BindingFlags.Instance | BindingFlags.NonPublic) ??
            throw new MissingFieldException(owner.GetType().FullName, fieldName);
        var snapshot = field.GetValue(owner) ??
            throw new InvalidOperationException($"{fieldName} was null.");
        return (int)(snapshot.GetType().GetProperty("Count")?.GetValue(snapshot) ??
            throw new MissingMemberException(snapshot.GetType().FullName, "Count"));
    }

    private static int GetGeneratedCodecCount(object owner)
    {
        var runtimeContext = owner.GetType().GetField(
                "_runtimeContext",
                BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner) ??
            throw new MissingFieldException(owner.GetType().FullName, "_runtimeContext");
        var snapshot = runtimeContext.GetType().GetMethod(
                "CreateGeneratedCodecSnapshot",
                BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(runtimeContext, null) ??
            throw new MissingMethodException(runtimeContext.GetType().FullName, "CreateGeneratedCodecSnapshot");
        return (int)(snapshot.GetType().GetProperty("Count")?.GetValue(snapshot) ??
            throw new MissingMemberException(snapshot.GetType().FullName, "Count"));
    }

    private static void AssertApi4Rejection(
        SharpLinkAssemblyRegistrationError? error,
        Assembly assembly,
        string entry)
    {
        Ensure(error?.Code == SharpLinkAssemblyRegistrationErrorCode.IncompatibleManifest,
            $"{entry} should reject API 4 as incompatible: {error}");
        Ensure(error!.Message.Contains(
                   $"API 4/{SharpLinkGeneratedManifestVersions.Api}",
                   StringComparison.Ordinal) &&
               error.Message.Contains(
                   $"Protocol 2/{SharpLinkGeneratedManifestVersions.Protocol}",
                   StringComparison.Ordinal) &&
               error.Message.Contains("Generator", StringComparison.Ordinal) &&
               error.Message.Contains("delete stale generated outputs", StringComparison.Ordinal) &&
               error.Message.Contains("regenerate and rebuild", StringComparison.Ordinal) &&
               error.Message.Contains("SharpLink SDK", StringComparison.Ordinal),
            $"{entry} should identify both version axes, Generator, and the migration action");
        Ensure(error.IncomingAssembly == assembly.FullName,
            $"{entry} should identify the incoming Assembly");
        Ensure(error.IncomingLoadContext == SharpLinkAssemblyManifestLoader.GetLoadContextIdentity(assembly),
            $"{entry} should identify the incoming collectible ALC");
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
            ISharpLinkMultiClusterClient multiClient,
            CancellationTokenSource serverCancellation,
            Task serverTask)
        {
            Server = server;
            Client = client;
            MultiClient = multiClient;
            _serverCancellation = serverCancellation;
            _serverTask = serverTask;
        }

        internal ISharpLinkServer Server { get; }

        internal ISharpLinkClient Client { get; }

        internal ISharpLinkMultiClusterClient MultiClient { get; }

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
            var multiClient = SharpLinkMultiClusterClientBuilder.Create()
                .AddCluster(
                    "plugins",
                    child => child.UseTcp(IPAddress.Loopback.ToString(), port),
                    slot => slot.AllowDynamicContracts = true)
                .Build();
            return new FixtureHarness(server, client, multiClient, cancellation, serverTask);
        }

        public async ValueTask DisposeAsync()
        {
            await MultiClient.StopAsync();
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
