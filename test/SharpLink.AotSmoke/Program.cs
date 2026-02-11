using System;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.AotSmoke;

public static class Program
{
    public static async Task<int> Main()
    {
        var port = GetFreePort();
        var cts = new CancellationTokenSource();
        var runToken = cts.Token;

        var server = SharpLinkServerBuilder.Create()
            .AddService<IAotService, AotService>()
            .UseTcp(port, IPAddress.Loopback.ToString())
            .UseSerializer(new MemoryPackSerializerAdaptor())
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
            .UseSerializer(new MemoryPackSerializerAdaptor())
            .Build();

        try
        {
            if (!await client.ConnectAsync(runToken))
                throw new Exception("connect failed");

            var svc = client.Get<IAotService>();
            var result = await svc.PingAsync();
            if (result != "pong")
                throw new Exception($"unexpected result: {result}");

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

public interface IAotService : IService
{
    ValueTask<string> PingAsync();
}

[RpcService]
public class AotService : IAotService
{
    public ValueTask<string> PingAsync() => ValueTask.FromResult("pong");
}
