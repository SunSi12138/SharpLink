using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpPack;

[assembly: SharpLinkClusterContractAssembly(
    "runtime",
    typeof(SharpLink.PackageSmoke.IPackageSmokeService))]

namespace SharpLink.PackageSmoke;

[RpcContract]
public interface IPackageSmokeService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);

    [NonCancellable]
    ValueTask<PackageSmokeEnvelope> EchoAsync(PackageSmokeEnvelope value);
}

[SharpPackable]
public sealed partial class PackageSmokeAddress
{
    public string City { get; set; } = string.Empty;
    public int PostalCode { get; set; }
}

[SharpPackable]
public sealed partial class PackageSmokeEnvelope
{
    public string Name { get; set; } = string.Empty;
    public PackageSmokeAddress Address { get; set; } = new();
    public List<int> Values { get; set; } = [];
}

[RpcService]
public sealed class PackageSmokeService : IPackageSmokeService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public ValueTask<PackageSmokeEnvelope> EchoAsync(PackageSmokeEnvelope value) =>
        ValueTask.FromResult(value);
}

public static class Program
{
    public static async Task Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await RunTransportSmokeAsync(useSharedMemory: false, timeout.Token);
        await RunTransportSmokeAsync(useSharedMemory: true, timeout.Token);
        await RunStaticEndpointSmokeAsync(timeout.Token);
        await RunReferencedAssemblyPackageSmokeAsync(timeout.Token);
    }

    private static async Task RunTransportSmokeAsync(
        bool useSharedMemory,
        CancellationToken cancellationToken)
    {
        var sharedMemoryName = $"sharplink-package-smoke-{Guid.NewGuid():N}";
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64));
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var localEndPoint = serverBuilder.Transport!.LocalEndPoint as IPEndPoint;
        if (!useSharedMemory && localEndPoint is null)
            throw new InvalidOperationException("Package smoke server did not expose its TCP endpoint.");
        var server = serverBuilder.Build();
        var serverTask = RunServerAsync(server, cancellationToken);

        var clientBuilder = SharpClientBuilder.Create()
            .UseRuntime(ConfigureCompression);
        if (useSharedMemory)
            clientBuilder.UseSharedMemory(sharedMemoryName);
        else
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), localEndPoint!.Port);
        var client = clientBuilder.Build();

        try
        {
            await client.ConnectAsync(cancellationToken);
            var fixedReadiness = await client.WaitForReadinessAsync(1, cancellationToken);
            VerifyReadinessSnapshot(
                fixedReadiness,
                expectedActiveEndpoints: 1,
                expectedTargetReadyEndpoints: 1);
            VerifyReadinessSnapshot(
                client.GetReadinessSnapshot(),
                expectedActiveEndpoints: 1,
                expectedTargetReadyEndpoints: 1);

            var proxy = client.Get<IPackageSmokeService>();
            var result = await proxy.AddAsync(20, 22);
            if (result != 42)
                throw new InvalidOperationException($"Package smoke returned {result} instead of 42.");

            var expected = new PackageSmokeEnvelope
            {
                Name = new string('p', 4096),
                Address = new PackageSmokeAddress { City = "Shanghai", PostalCode = 200000 },
                Values = [1, 2, 3]
            };
            var actual = await proxy.EchoAsync(expected);
            if (actual.Name != expected.Name ||
                actual.Address.City != expected.Address.City ||
                actual.Address.PostalCode != expected.Address.PostalCode ||
                !actual.Values.SequenceEqual(expected.Values))
                throw new InvalidOperationException("Package smoke generated DTO codec round-trip failed.");

            if (!useSharedMemory)
                await RunRuntimeMultiClusterSmokeAsync(localEndPoint!.Port, cancellationToken);
        }
        finally
        {
            await client.DisposeAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private static async Task RunRuntimeMultiClusterSmokeAsync(
        int port,
        CancellationToken cancellationToken)
    {
        await using var client = SharpLinkMultiClusterClientBuilder.Create()
            .AddCluster(
                "bootstrap",
                child => child.UseTcp(IPAddress.Loopback.ToString(), port),
                slot => slot.AllowDynamicContracts = true)
            .Build();
        await client.ConnectAsync(cancellationToken);

        await client.AddClusterAsync(
            "runtime",
            child => child.UseTcp(IPAddress.Loopback.ToString(), port),
            cancellationToken: cancellationToken);
        if (await client.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
            throw new InvalidOperationException("Runtime multi-cluster Add package smoke failed.");

        await client.ReplaceClusterAsync(
            "runtime",
            child => child.UseTcp(IPAddress.Loopback.ToString(), port),
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (await client.Get<IPackageSmokeService>().AddAsync(19, 23) != 42)
            throw new InvalidOperationException("Runtime multi-cluster Replace package smoke failed.");

        var removal = await client.RemoveClusterAsync(
            "runtime",
            TimeSpan.FromSeconds(2),
            cancellationToken);
        if (!removal.Succeeded || !removal.ReferencesReleased || removal.ForcedStop)
            throw new InvalidOperationException("Runtime multi-cluster Remove package smoke failed.");
    }

    private static async Task RunStaticEndpointSmokeAsync(CancellationToken cancellationToken)
    {
        var firstBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64))
            .UseTcp(0, IPAddress.Loopback.ToString());
        var secondBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(options => options.Global.UseConcurrency(64))
            .UseTcp(0, IPAddress.Loopback.ToString());
        var firstPort = ((IPEndPoint)firstBuilder.Transport!.LocalEndPoint!).Port;
        var secondPort = ((IPEndPoint)secondBuilder.Transport!.LocalEndPoint!).Port;
        var firstServer = firstBuilder.Build();
        var secondServer = secondBuilder.Build();
        var firstTask = RunServerAsync(firstServer, cancellationToken);
        var secondTask = RunServerAsync(secondServer, cancellationToken);
        var endpoints = new[]
        {
            new SharpLinkEndpoint
            {
                Id = "first",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), firstPort),
                Attributes = new Dictionary<string, string> { ["zone"] = "a" }
            },
            new SharpLinkEndpoint
            {
                Id = "second",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), secondPort),
                Attributes = new Dictionary<string, string> { ["zone"] = "b" }
            }
        };
        var client = SharpClientBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseEndpoints(
                endpoints,
                SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
            .Build();

        try
        {
            await client.ConnectAsync(cancellationToken);
            var staticReadiness = await client.WaitForReadinessAsync(2, cancellationToken);
            VerifyReadinessSnapshot(
                staticReadiness,
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            VerifyReadinessSnapshot(
                client.GetReadinessSnapshot(),
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            if (await client.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
                throw new InvalidOperationException("Static endpoint package smoke returned an unexpected result.");

            await using var dynamicClient = SharpClientBuilder.Create()
                .UseRuntime(ConfigureCompression)
                .UseEndpointResolver(
                    new DelegateSharpLinkEndpointResolver(
                        _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1, endpoints))),
                    SharpLinkTransportFactories.Sockets())
                .UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin)
                .Build();
            await dynamicClient.ConnectAsync(cancellationToken);
            var dynamicReadiness = await dynamicClient.WaitForReadinessAsync(2, cancellationToken);
            VerifyReadinessSnapshot(
                dynamicReadiness,
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            VerifyReadinessSnapshot(
                dynamicClient.GetReadinessSnapshot(),
                expectedActiveEndpoints: 2,
                expectedTargetReadyEndpoints: 2);
            if (await dynamicClient.Get<IPackageSmokeService>().AddAsync(20, 22) != 42)
                throw new InvalidOperationException("Dynamic endpoint package smoke returned an unexpected result.");
        }
        finally
        {
            await client.DisposeAsync();
            await firstServer.DisposeAsync();
            await secondServer.DisposeAsync();
            await Task.WhenAny(firstTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            await Task.WhenAny(secondTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
        }
    }

    private static void VerifyReadinessSnapshot(
        SharpLinkClientReadinessSnapshot snapshot,
        int expectedActiveEndpoints,
        int expectedTargetReadyEndpoints)
    {
        if (!snapshot.MeetsTarget ||
            snapshot.State != SharpLinkConnectionState.Ready ||
            snapshot.ActiveEndpoints != expectedActiveEndpoints ||
            snapshot.ReadyEndpoints != expectedActiveEndpoints ||
            snapshot.ReadyConnections < snapshot.ReadyEndpoints ||
            snapshot.TargetReadyEndpoints != expectedTargetReadyEndpoints)
        {
            throw new InvalidOperationException($"Unexpected Client readiness snapshot: {snapshot}.");
        }
    }

    private static async Task RunServerAsync(ISharpLinkServer server, CancellationToken cancellationToken)
    {
        try
        {
            await server.RunAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task RunReferencedAssemblyPackageSmokeAsync(CancellationToken cancellationToken)
    {
        var sharedMemoryName = $"sharplink-package-reference-rooting-{Guid.NewGuid():N}";
        var serverAssembly = FindReferenceRootingAssembly(
            "SharpLink.ReferenceRooting.PackageServer",
            "SharpLink.ReferenceRooting.PackageServer.dll");
        var clientAssembly = FindReferenceRootingAssembly(
            "SharpLink.ReferenceRooting.PackageClient",
            "SharpLink.ReferenceRooting.PackageClient.dll");
        using var server = StartReferenceRootingProcess(serverAssembly, sharedMemoryName);
        try
        {
            var ready = await server.StandardOutput.ReadLineAsync(cancellationToken);
            if (!string.Equals(ready, "PACKAGE_REFERENCE_ROOTING_SERVER_READY", StringComparison.Ordinal))
                throw new InvalidOperationException($"Referenced package server did not become ready: '{ready}'.");

            using var client = StartReferenceRootingProcess(clientAssembly, sharedMemoryName);
            var clientOutput = await client.StandardOutput.ReadToEndAsync(cancellationToken);
            var clientError = await client.StandardError.ReadToEndAsync(cancellationToken);
            await client.WaitForExitAsync(cancellationToken);
            if (client.ExitCode != 0 ||
                !clientOutput.Contains("PACKAGE_REFERENCE_ROOTING_PASS", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Referenced package client failed ({client.ExitCode}): {clientOutput} {clientError}");
            }
            Console.WriteLine("PACKAGE_REFERENCE_ROOTING_PASS");
        }
        finally
        {
            if (!server.HasExited)
                server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync(CancellationToken.None);
        }
    }

    private static Process StartReferenceRootingProcess(string assemblyPath, string sharedMemoryName)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(assemblyPath);
        start.ArgumentList.Add(sharedMemoryName);
        return Process.Start(start) ?? throw new InvalidOperationException(
            $"Could not start reference-rooting package process '{assemblyPath}'.");
    }

    private static string FindReferenceRootingAssembly(string projectName, string assemblyName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
            directory = directory.Parent;
        if (directory is null)
            throw new DirectoryNotFoundException("Could not locate the SharpLink repository root.");

        var path = Path.Combine(
            directory.FullName,
            "test",
            projectName,
            "bin",
            "Release",
            "net10.0",
            assemblyName);
        if (!File.Exists(path))
            throw new FileNotFoundException("The reference-rooting package smoke assembly was not built.", path);
        return path;
    }

    private static void ConfigureCompression(SharpLinkRuntimeOptions options)
        => options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());
}
