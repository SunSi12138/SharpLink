using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

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
        if (role == "server")
            return await RunServerOnlyAsync(sharedMemoryName).ConfigureAwait(false);
        if (role == "client")
            return await RunClientOnlyAsync(sharedMemoryName).ConfigureAwait(false);
        if (role != "local")
            throw new ArgumentException($"Unsupported AOT smoke role '{role}'.");

        var cts = new CancellationTokenSource();
        var runToken = cts.Token;

        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(ConfigureAdmission);
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var port = useSharedMemory
            ? 0
            : ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
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
            client = SharpClientBuilder.Create()
                .UseRuntime(ConfigureCompression)
                .UseSharedMemory(sharedMemoryName)
                .Build();
        }
        else
        {
            client = SharpClientBuilder.Create()
                .UseRuntime(ConfigureCompression)
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

    private static async Task<int> RunServerOnlyAsync(string name)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var server = SharpLinkServerBuilder.Create()
            .UseSharedMemory(name)
            .UseRuntime(ConfigureCompression)
            .UseAdmissionControl(ConfigureAdmission)
            .Build();
        VerifyRuntimeAssemblyBoundary(server);
        var runTask = server.RunAsync(timeout.Token).AsTask();
        Console.WriteLine("AOT_SMOKE_SERVER_READY");
        try
        {
            await AotService.FinalCall.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
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
        await using var client = SharpClientBuilder.Create()
            .UseSharedMemory(name)
            .UseRuntime(ConfigureCompression)
            .Build();
        try
        {
            await VerifyClientAsync(client, CancellationToken.None).ConfigureAwait(false);
            Console.WriteLine("AOT_SMOKE_CLIENT_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"AOT_SMOKE_CLIENT_FAIL: {exception}");
            return 1;
        }
    }

    private static async Task VerifyClientAsync(ISharpLinkClient client, CancellationToken cancellationToken)
    {
        VerifyRuntimeAssemblyBoundary(client);
        await client.ConnectAsync(cancellationToken).ConfigureAwait(false);

        var health = await client.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
        if (health.Status != SharpLinkHealthStatus.Ready)
            throw new Exception($"unexpected health status: {health.Status}");

        var svc = client.Get<IAotService>();
        var result = await svc.PingAsync().ConfigureAwait(false);
        if (result != "pong")
            throw new Exception($"unexpected result: {result}");

        var profileName = new string('a', 4096);
        var profile = new UserProfile
        {
            Name = profileName,
            Tags = ["rpc", "aot", "smoke"]
        };
        var profileEcho = await svc.EchoProfileAsync(profile).ConfigureAwait(false);
        if (profileEcho.Name != profileName || profileEcho.Tags.Length != 3 || profileEcho.Tags[2] != "smoke")
            throw new Exception("unexpected profile echo");

        var ints = await svc.ReverseIntsAsync([1, 2, 3, 4]).ConfigureAwait(false);
        if (ints.Length != 4 || ints[0] != 4 || ints[3] != 1)
            throw new Exception("unexpected int[] echo");

        var nested = await svc.EchoNestedStringsAsync([["a", "b"], ["c"]]).ConfigureAwait(false);
        if (nested.Length != 2 || nested[0].Length != 2 || nested[1][0] != "c")
            throw new Exception("unexpected string[][] echo");

        var moved = await svc.OffsetAsync(new Point2D { X = 3, Y = 7 }, 2, -5).ConfigureAwait(false);
        if (moved.X != 5 || moved.Y != 2)
            throw new Exception("unexpected struct result");

        var pair = await svc.EchoPairAsync(new AotPair(7, "pair")).ConfigureAwait(false);
        if (pair.Number != 14 || pair.Text != "pair-ok")
            throw new Exception("unexpected pair result");
    }

    private static void VerifyRuntimeAssemblyBoundary(ISharpLinkClient client)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return;
        var result = client.RegisterAssembly(typeof(Program).Assembly);
        if (result.Succeeded || result.Error?.Code != SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported)
            throw new Exception($"unexpected NativeAOT client registration result: {result.Error}");
    }

    private static void VerifyRuntimeAssemblyBoundary(ISharpLinkServer server)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
            return;
        var result = server.RegisterAssembly(typeof(Program).Assembly);
        if (result.Succeeded || result.Error?.Code != SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported)
            throw new Exception($"unexpected NativeAOT server registration result: {result.Error}");
    }

    private static void ConfigureCompression(SharpLinkRuntimeOptions options)
        => options.Compression.Providers.Add(SharpLinkCompressionProviders.CreateBrotli());

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
    ValueTask<int[]> ReverseIntsAsync(int[] values);
    [NonCancellable]
    ValueTask<string[][]> EchoNestedStringsAsync(string[][] values);
    [NonCancellable]
    ValueTask<Point2D> OffsetAsync(Point2D point, int dx, int dy);
    [NonCancellable]
    ValueTask<AotPair> EchoPairAsync(AotPair value);
}

[RpcService]
public class AotService : IAotService
{
    internal static TaskCompletionSource<bool> FinalCall { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<string> PingAsync() => ValueTask.FromResult("pong");

    public ValueTask<UserProfile> EchoProfileAsync(UserProfile profile) => ValueTask.FromResult(profile);

    public ValueTask<int[]> ReverseIntsAsync(int[] values)
    {
        Array.Reverse(values);
        return ValueTask.FromResult(values);
    }

    public ValueTask<string[][]> EchoNestedStringsAsync(string[][] values) => ValueTask.FromResult(values);

    public ValueTask<Point2D> OffsetAsync(Point2D point, int dx, int dy)
        => ValueTask.FromResult(new Point2D { X = point.X + dx, Y = point.Y + dy });

    public ValueTask<AotPair> EchoPairAsync(AotPair value)
    {
        FinalCall.TrySetResult(true);
        return ValueTask.FromResult(value with { Number = value.Number * 2, Text = value.Text + "-ok" });
    }
}

public sealed class UserProfile
{
    public string Name { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
}

public sealed record AotPair(int Number, string Text);

public struct Point2D
{
    public int X { get; set; }
    public int Y { get; set; }
}
