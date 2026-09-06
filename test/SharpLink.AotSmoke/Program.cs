using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.AotContracts;
using SharpLink.Client;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;
using SharpPack;

[assembly: SharpLinkClusterContractAssembly("orders", typeof(SharpLink.AotSmoke.IAotService))]
[assembly: SharpLinkClusterContractAssembly("payments", typeof(ISecondAotService))]

namespace SharpLink.AotSmoke;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var useSharedMemory = args.Any(static value =>
            value.Equals("sharedmemory", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("shared-memory", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("shm", StringComparison.OrdinalIgnoreCase));
        var sharedMemoryName = "sharplink-aot-smoke";
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--shm-name", StringComparison.OrdinalIgnoreCase))
                sharedMemoryName = args[index + 1];
        }
        var role = "local";
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--role", StringComparison.OrdinalIgnoreCase))
                role = args[index + 1].ToLowerInvariant();
        }
        string? completionFile = null;
        for (var index = 0; index + 1 < args.Length; index++)
        {
            if (args[index].Equals("--completion-file", StringComparison.OrdinalIgnoreCase))
                completionFile = args[index + 1];
        }
        if (role == "server")
            return await RunServerOnlyAsync(sharedMemoryName, completionFile).ConfigureAwait(false);
        if (role == "client")
            return await RunClientOnlyAsync(sharedMemoryName).ConfigureAwait(false);
        if (role != "local")
            throw new ArgumentException($"Unsupported AOT smoke role '{role}'.");

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var runToken = cts.Token;

        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseAdmissionControl(ConfigureAdmission)
            .UseRuntime(ConfigureZstd);
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var port = useSharedMemory
            ? 0
            : ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        VerifyReferencedServiceManifestIsRootedBeforeBuild();
        var server = serverBuilder.Build();
        VerifyRuntimeAssemblyBoundary(server);

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(runToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        ISharpLinkClient client;
        if (useSharedMemory)
        {
            client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseRuntime(ConfigureZstd)
                .UseSharedMemory(sharedMemoryName)
                .Build();
        }
        else
        {
            client = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseRuntime(ConfigureZstd)
                .UseEndpointResolver(
                    new DelegateSharpLinkEndpointResolver(
                        _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1,
                        [
                            new SharpLinkEndpoint
                            {
                                Id = "aot-local",
                                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
                            }
                        ]))),
                    SharpLinkTransportFactories.Sockets())
                .Build();
        }

        try
        {
            await VerifyClientAsync(client, runToken).ConfigureAwait(false);
            if (!useSharedMemory)
                await VerifyStaticReadinessClientAsync(port, runToken).ConfigureAwait(false);
            await using var multiClusterClient = CreateMultiClusterClient(useSharedMemory, sharedMemoryName, port);
            await VerifyMultiClusterClientAsync(multiClusterClient, runToken).ConfigureAwait(false);

            Console.WriteLine($"AOT_SMOKE_PASS transport={(useSharedMemory ? "sharedmemory" : "tcp")}");
            return 0;
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"AOT_SMOKE_FAIL: {ex}");
            return 1;
        }
        finally
        {
            await cts.CancelAsync();
            await client.DisposeAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
            cts.Dispose();
        }
    }

    private static async Task<int> RunServerOnlyAsync(string name, string? completionFile)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        VerifyReferencedServiceManifestIsRootedBeforeBuild();
        await using var server = SharpLinkServerBuilder.Create()
            .UseSharedMemory(name)
            .UseAdmissionControl(ConfigureAdmission)
            .UseRuntime(ConfigureZstd)
            .Build();
        VerifyRuntimeAssemblyBoundary(server);
        var runTask = server.RunAsync(timeout.Token).AsTask();
        Console.WriteLine("AOT_SMOKE_SERVER_READY");
        try
        {
            if (completionFile is null)
                await AotService.FinalCall.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            else
                await WaitForCompletionFileAsync(completionFile, timeout.Token).ConfigureAwait(false);
            await server.StopAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
            await runTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            Console.WriteLine("AOT_SMOKE_SERVER_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"AOT_SMOKE_SERVER_FAIL: {exception}");
            return 1;
        }
    }

    private static async Task<int> RunClientOnlyAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseRuntime(ConfigureZstd)
            .UseSharedMemory(name)
            .Build();
        try
        {
            await VerifyClientAsync(client, timeout.Token).ConfigureAwait(false);
            Console.WriteLine("AOT_SMOKE_CLIENT_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"AOT_SMOKE_CLIENT_FAIL: {exception}");
            return 1;
        }
    }

    private static async Task WaitForCompletionFileAsync(
        string completionFile,
        CancellationToken cancellationToken)
    {
        while (!File.Exists(completionFile))
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyClientAsync(ISharpLinkClient client, CancellationToken cancellationToken)
    {
        VerifyRuntimeAssemblyBoundary(client);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var readiness = await client.WaitForReadinessAsync(1, cancellationToken).ConfigureAwait(false);
        if (!readiness.MeetsTarget ||
            readiness.State != SharpLinkConnectionState.Ready ||
            readiness.ActiveEndpoints != 1 ||
            readiness.ReadyEndpoints != 1 ||
            readiness.ReadyConnections < 1 ||
            readiness.TargetReadyEndpoints != 1 ||
            client.GetReadinessSnapshot() != readiness)
        {
            throw new Exception($"unexpected Client readiness snapshot: {readiness}");
        }

        var health = await client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (health.Status != SharpLinkHealthStatus.Ready)
            throw new Exception($"unexpected health status: {health.Status}");

        var svc = client.Get<IAotService>();
        var result = await svc.PingAsync().ConfigureAwait(false);
        if (result != "pong")
            throw new Exception($"unexpected result: {result}");

        var referencedService = client.Get<IReferencedAssemblyService>();
        var referencedResult = await referencedService.IdentifyAsync().ConfigureAwait(false);
        if (referencedResult != "internal-referenced-service")
            throw new Exception($"unexpected referenced service result: {referencedResult}");
        Console.WriteLine("REFERENCED_SERVICE_PASS");

        var profileName = new string('a', 4096);
        var profile = new UserProfile
        {
            Name = profileName,
            Tags = ["rpc", "aot", "smoke"]
        };
        var profileEcho = await svc.EchoProfileAsync(profile).ConfigureAwait(false);
        if (profileEcho.Name != profileName || profileEcho.Tags.Length != 3 || profileEcho.Tags[2] != "smoke")
            throw new Exception("unexpected profile echo");

        var graph = new AotSharpPackGraph
        {
            Name = "root-中文",
            Values = [1, 2, 3],
            Children = [new AotSharpPackGraph { Name = "child", Values = [4, 5] }]
        };
        graph.Parent = graph;
        var graphEcho = await svc.EchoSharpPackGraphAsync(graph).ConfigureAwait(false);
        if (graphEcho.Name != "root-中文" ||
            graphEcho.Values.Count != 3 ||
            graphEcho.Children.Count != 1 ||
            graphEcho.Children[0].Values.Count != 2 ||
            !ReferenceEquals(graphEcho, graphEcho.Parent))
        {
            throw new Exception("unexpected SharpPack nested/circular/collection echo");
        }

        var ints = await svc.ReverseIntsAsync([1, 2, 3, 4]).ConfigureAwait(false);
        if (ints.Length != 4 || ints[0] != 4 || ints[3] != 1)
            throw new Exception("unexpected int[] echo");

        var nested = await svc.EchoNestedStringsAsync([["a", "b"], ["c"]]).ConfigureAwait(false);
        if (nested.Length != 2 || nested[0].Length != 2 || nested[1][0] != "c")
            throw new Exception("unexpected string[][] echo");

        var moved = await svc.OffsetAsync(new Point2D { X = 3, Y = 7 }, 2, -5).ConfigureAwait(false);
        if (moved.X != 5 || moved.Y != 2)
            throw new Exception("unexpected struct result");

        await svc.NotifyAsync(37).ConfigureAwait(false);

        var uploadSum = await svc.SumAsync(ToAsyncEnumerable([3, 5, 7])).ConfigureAwait(false);
        if (uploadSum != 15)
            throw new Exception($"unexpected client-streaming sum: {uploadSum}");

        var range = await CollectAsync(svc.RangeAsync(4)).ConfigureAwait(false);
        if (!range.SequenceEqual([0, 1, 2, 3]))
            throw new Exception("unexpected server-streaming values");

        var duplex = await CollectAsync(svc.MultiplyStreamAsync(
            ToAsyncEnumerable([2, 4, 6]),
            3)).ConfigureAwait(false);
        if (!duplex.SequenceEqual([6, 12, 18]))
            throw new Exception("unexpected duplex-streaming values");

        var pair = await svc.EchoPairAsync(new AotPair(7, "pair")).ConfigureAwait(false);
        if (pair.Number != 14 || pair.Text != "pair-ok")
            throw new Exception("unexpected pair result");
    }

    private static async Task VerifyStaticReadinessClientAsync(
        int port,
        CancellationToken cancellationToken)
    {
        var endpoints = new[]
        {
            new SharpLinkEndpoint
            {
                Id = "aot-static-first",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
            },
            new SharpLinkEndpoint
            {
                Id = "aot-static-second",
                Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
            }
        };
        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseRuntime(ConfigureZstd)
            .UseEndpoints(endpoints, SharpLinkTransportFactories.Sockets())
            .UseCluster(options =>
            {
                options.MinReadyEndpoints = 2;
                options.MaxConnections = 2;
                options.MaxConnectionsPerEndpoint = 1;
            })
            .Build();

        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var readiness = await client.WaitForReadinessAsync(2, cancellationToken).ConfigureAwait(false);
        if (!readiness.MeetsTarget ||
            readiness.State != SharpLinkConnectionState.Ready ||
            readiness.ActiveEndpoints != 2 ||
            readiness.ReadyEndpoints != 2 ||
            readiness.ReadyConnections != 2 ||
            readiness.TargetReadyEndpoints != 2 ||
            client.GetReadinessSnapshot() != readiness)
        {
            throw new Exception($"unexpected static Client readiness snapshot: {readiness}");
        }
        if (await client.Get<IAotService>().PingAsync().ConfigureAwait(false) != "pong")
            throw new Exception("unexpected static Client AOT result");
        Console.WriteLine("STATIC_READINESS_PASS");
    }

    private static async IAsyncEnumerable<int> ToAsyncEnumerable(IEnumerable<int> values)
    {
        foreach (var value in values)
        {
            yield return value;
            await Task.Yield();
        }
    }

    private static async Task<List<int>> CollectAsync(IAsyncEnumerable<int> values)
    {
        var result = new List<int>();
        await foreach (var value in values.ConfigureAwait(false))
            result.Add(value);
        return result;
    }

    private static ISharpLinkMultiClusterClient CreateMultiClusterClient(
        bool useSharedMemory,
        string sharedMemoryName,
        int port)
        => SharpLinkMultiClusterClientBuilder.Create().DisableRequestTimeout()
            .AddCluster(
                "orders",
                child => ConfigureClientTransport(child, useSharedMemory, sharedMemoryName, port),
                slot => slot.AllowDynamicContracts = true)
            .AddCluster("payments", child => ConfigureClientTransport(child, useSharedMemory, sharedMemoryName, port))
            .Build();

    private static void ConfigureClientTransport(
        SharpClientBuilder builder,
        bool useSharedMemory,
        string sharedMemoryName,
        int port)
    {
        builder.UseRuntime(ConfigureZstd);
        if (useSharedMemory)
            builder.UseSharedMemory(sharedMemoryName);
        else
            builder.UseTcp(IPAddress.Loopback.ToString(), port);
    }

    private static async Task VerifyMultiClusterClientAsync(
        ISharpLinkMultiClusterClient client,
        CancellationToken cancellationToken)
    {
        VerifyRuntimeAssemblyBoundary(client);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var ordersHealth = await client.CheckHealthAsync("orders", cancellationToken).ConfigureAwait(false);
        var paymentsHealth = await client.CheckHealthAsync("payments", cancellationToken).ConfigureAwait(false);
        if (ordersHealth.Status != SharpLinkHealthStatus.Ready || paymentsHealth.Status != SharpLinkHealthStatus.Ready)
            throw new Exception("static multi-cluster health did not reach Ready");

        var orders = client.Get<IAotService>();
        if (await orders.PingAsync().ConfigureAwait(false) != "pong")
            throw new Exception("unexpected orders multi-cluster result");

        var payments = client.Get<ISecondAotService>();
        if (await payments.MultiplyAsync(21).ConfigureAwait(false) != 42)
            throw new Exception("unexpected payments multi-cluster result");
    }

    private static void VerifyRuntimeAssemblyBoundary(ISharpLinkClient client)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return;
        var result = client.RegisterAssembly(typeof(Program).Assembly);
        if (result.Succeeded || result.Error?.Code != SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported)
            throw new Exception($"unexpected NativeAOT client registration result: {result.Error}");
    }

    private static void VerifyRuntimeAssemblyBoundary(ISharpLinkMultiClusterClient client)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return;
        var result = client.RegisterAssembly("orders", typeof(Program).Assembly);
        if (result.Succeeded || result.Error?.Code != SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported)
            throw new Exception($"unexpected NativeAOT multi-cluster registration result: {result.Error}");
    }

    private static void VerifyRuntimeAssemblyBoundary(ISharpLinkServer server)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return;
        var result = server.RegisterAssembly(typeof(Program).Assembly);
        if (result.Succeeded || result.Error?.Code != SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported)
            throw new Exception($"unexpected NativeAOT server registration result: {result.Error}");
    }

    private static void VerifyReferencedServiceManifestIsRootedBeforeBuild()
    {
        var manifests = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot()
            .Where(static manifest =>
                string.Equals(
                    manifest.OwnerAssembly.GetName().Name,
                    "SharpLink.AotServices",
                    StringComparison.Ordinal))
            .ToArray();
        if (manifests.Length != 1)
            throw new Exception($"expected one rooted SharpLink.AotServices manifest, found {manifests.Length}");

        var services = manifests[0].Services
            .Where(static service => service.ContractType == typeof(IReferencedAssemblyService))
            .ToArray();
        if (services.Length != 1 || services[0].ImplementationType.IsPublic)
            throw new Exception("referenced internal service manifest was not rooted before server Build");
    }

    private static void ConfigureZstd(SharpLinkRuntimeOptions options)
    {
        options.Compression.Providers.Add(new SharpLinkZstdCompressionProvider());
    }

    private static void ConfigureAdmission(SharpLinkAdmissionControlOptions options)
        => options.Global.UseConcurrency(64);
}

[RpcContract]
public interface IAotService : IService
{
    [NonCancellable]
    ValueTask<string> PingAsync();
    [NonCancellable]
    ValueTask<UserProfile> EchoProfileAsync(UserProfile profile);
    [NonCancellable]
    ValueTask<AotSharpPackGraph> EchoSharpPackGraphAsync(AotSharpPackGraph value);
    [NonCancellable]
    ValueTask<int[]> ReverseIntsAsync(int[] values);
    [NonCancellable]
    ValueTask<string[][]> EchoNestedStringsAsync(string[][] values);
    [NonCancellable]
    ValueTask<Point2D> OffsetAsync(Point2D point, int dx, int dy);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(int value);
    [NonCancellable]
    ValueTask<int> SumAsync(IAsyncEnumerable<int> values);
    [NonCancellable]
    IAsyncEnumerable<int> RangeAsync(int count);
    [NonCancellable]
    IAsyncEnumerable<int> MultiplyStreamAsync(IAsyncEnumerable<int> values, int factor);
    [NonCancellable]
    ValueTask<AotPair> EchoPairAsync(AotPair value);
}

[RpcService]
public class AotService : IAotService
{
    private static readonly TaskCompletionSource<int> NotificationObserved =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal static TaskCompletionSource<bool> FinalCall { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<string> PingAsync() => ValueTask.FromResult("pong");

    public ValueTask<UserProfile> EchoProfileAsync(UserProfile profile) => ValueTask.FromResult(profile);

    public ValueTask<AotSharpPackGraph> EchoSharpPackGraphAsync(AotSharpPackGraph value)
        => ValueTask.FromResult(value);

    public ValueTask<int[]> ReverseIntsAsync(int[] values)
    {
        Array.Reverse(values);
        return ValueTask.FromResult(values);
    }

    public ValueTask<string[][]> EchoNestedStringsAsync(string[][] values) => ValueTask.FromResult(values);

    public ValueTask<Point2D> OffsetAsync(Point2D point, int dx, int dy)
        => ValueTask.FromResult(new Point2D { X = point.X + dx, Y = point.Y + dy });

    public ValueTask NotifyAsync(int value)
    {
        NotificationObserved.TrySetResult(value);
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> SumAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        await foreach (var value in values.ConfigureAwait(false))
            sum += value;
        return sum;
    }

    public async IAsyncEnumerable<int> RangeAsync(int count)
    {
        var notification = await NotificationObserved.Task.ConfigureAwait(false);
        if (notification != 37)
            throw new InvalidOperationException($"unexpected one-way value: {notification}");
        for (var value = 0; value < count; value++)
            yield return value;
    }

    public async IAsyncEnumerable<int> MultiplyStreamAsync(
        IAsyncEnumerable<int> values,
        int factor)
    {
        await foreach (var value in values.ConfigureAwait(false))
            yield return value * factor;
    }

    public ValueTask<AotPair> EchoPairAsync(AotPair value)
    {
        FinalCall.TrySetResult(true);
        return ValueTask.FromResult(value with { Number = value.Number * 2, Text = value.Text + "-ok" });
    }
}

[RpcService]
public sealed class SecondAotService : ISecondAotService
{
    public ValueTask<int> MultiplyAsync(int value) => ValueTask.FromResult(value * 2);
}

public sealed class UserProfile
{
    public string Name { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
}

[SharpPackable(GenerateType.CircularReference)]
public sealed partial class AotSharpPackGraph
{
    [SharpPackOrder(0)] public string Name { get; set; } = string.Empty;
    [SharpPackOrder(1)] public AotSharpPackGraph? Parent { get; set; }
    [SharpPackOrder(2), SharpPackAllowSerialize] public List<AotSharpPackGraph> Children { get; set; } = [];
    [SharpPackOrder(3), SharpPackAllowSerialize] public List<int> Values { get; set; } = [];
}

public sealed record AotPair(int Number, string Text);

public struct Point2D
{
    public int X { get; set; }
    public int Y { get; set; }
}
