using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using MemoryPack;
using MemoryPack.Formatters;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.AotSmoke;

public static class Program
{
    public static async Task<int> Main()
    {
        MemoryPackFormatterProvider.Register(new ValueTupleFormatter<int,string>());
        
        var port = GetFreePort();
        var cts = new CancellationTokenSource();
        var runToken = cts.Token;

        var server = SharpLinkServerBuilder.Create()
            .AddService<IAotService, AotService>()
            .UseTcp(port, IPAddress.Loopback.ToString())
            .UseSerializer(MemoryPackCodec.Resolver)
            .Build();

        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.Start(runToken);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseSerializer(MemoryPackCodec.Resolver)
            .Build();

        try
        {
            if (!await client.ConnectAsync(runToken))
                throw new Exception("connect failed");

            var svc = client.Get<IAotService>();
            var result = await svc.PingAsync();
            if (result != "pong")
                throw new Exception($"unexpected result: {result}");

            var profile = new UserProfile
            {
                Name = "SharpLink",
                Tags = new[] { "rpc", "aot", "smoke" },
            };
            var profileEcho = await svc.EchoProfileAsync(profile);
            if (profileEcho.Name != "SharpLink" || profileEcho.Tags.Length != 3 || profileEcho.Tags[2] != "smoke")
                throw new Exception("unexpected profile echo");

            var ints = await svc.ReverseIntsAsync(new[] { 1, 2, 3, 4 });
            if (ints.Length != 4 || ints[0] != 4 || ints[3] != 1)
                throw new Exception("unexpected int[] echo");

            var nested = await svc.EchoNestedStringsAsync(new[] { new[] { "a", "b" }, new[] { "c" } });
            if (nested.Length != 2 || nested[0].Length != 2 || nested[1][0] != "c")
                throw new Exception("unexpected string[][] echo");

            var moved = await svc.OffsetAsync(new Point2D { X = 3, Y = 7 }, 2, -5);
            if (moved.X != 5 || moved.Y != 2)
                throw new Exception("unexpected struct result");

            var tuple = await svc.EchoTupleAsync((7, "tuple"));
            if (tuple.Item1 != 14 || tuple.Item2 != "tuple-ok")
                throw new Exception("unexpected tuple result");

            Console.WriteLine("AOT_SMOKE_PASS");
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
            (client as IDisposable)?.Dispose();
            (server as IDisposable)?.Dispose();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
            cts.Dispose();
        }
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}

[RpcContract]
public interface IAotService : IService
{
    ValueTask<string> PingAsync();
    ValueTask<UserProfile> EchoProfileAsync(UserProfile profile);
    ValueTask<int[]> ReverseIntsAsync(int[] values);
    ValueTask<string[][]> EchoNestedStringsAsync(string[][] values);
    ValueTask<Point2D> OffsetAsync(Point2D point, int dx, int dy);
    ValueTask<(int, string)> EchoTupleAsync((int, string) value);
}

[RpcService]
public class AotService : IAotService
{
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

    public ValueTask<(int, string)> EchoTupleAsync((int, string) value)
        => ValueTask.FromResult((value.Item1 * 2, value.Item2 + "-ok"));
}

[MemoryPackable]
public partial class UserProfile
{
    public string Name { get; set; } = string.Empty;
    public string[] Tags { get; set; } = Array.Empty<string>();
}

public struct Point2D
{
    public int X { get; set; }
    public int Y { get; set; }
}
