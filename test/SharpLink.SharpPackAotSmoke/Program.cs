using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.AotExternalPayloads;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Serializer.SharpPack;
using SharpLink.Server;

[assembly: RpcCodecAdapter(typeof(ExternalAotPayload), typeof(SharpPackRpcCodecAdapter))]

namespace SharpLink.SharpPackAotSmoke;

[RpcContract]
public interface IExternalSharpPackAotService : IService
{
    [NonCancellable]
    ValueTask<ExternalAotPayload> EchoAsync(ExternalAotPayload payload);
}

[RpcService]
internal sealed class ExternalSharpPackAotService : IExternalSharpPackAotService
{
    public ExternalSharpPackAotService()
    {
    }

    public ValueTask<ExternalAotPayload> EchoAsync(ExternalAotPayload payload)
        => ValueTask.FromResult(payload);
}

public static class Program
{
    public static async Task<int> Main()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString());
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        await using var server = serverBuilder.Build();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);

        await using var client = SharpClientBuilder.Create().DisableRequestTimeout()
            .UseEndpointResolver(
                new DelegateSharpLinkEndpointResolver(
                    _ => ValueTask.FromResult(new SharpLinkEndpointSnapshot(1,
                    [
                        new SharpLinkEndpoint
                        {
                            Id = "sharppack-sidecar-aot",
                            Address = new SharpLinkTcpAddress(IPAddress.Loopback.ToString(), port)
                        }
                    ]))),
                SharpLinkTransportFactories.Sockets())
            .Build();

        try
        {
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);
            var service = client.Get<IExternalSharpPackAotService>();
            var payload = new ExternalAotPayload
            {
                Id = 313,
                Children =
                [
                    new ExternalAotChild { Name = "first" },
                    new ExternalAotChild { Name = "第二" }
                ],
                ByName = new Dictionary<string, ExternalAotChild>
                {
                    ["primary"] = new ExternalAotChild { Name = "dictionary" }
                }
            };

            var echoed = await service.EchoAsync(payload).ConfigureAwait(false);
            if (echoed.Id != 313 ||
                echoed.Children.Count != 2 ||
                echoed.Children[1].Name != "第二" ||
                echoed.ByName.Count != 1 ||
                echoed.ByName["primary"].Name != "dictionary")
            {
                throw new Exception("unexpected external SharpPack sidecar echo");
            }

            Console.WriteLine("SHARPPACK_SIDECAR_AOT_PASS");
            return 0;
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"SHARPPACK_SIDECAR_AOT_FAIL: {exception}");
            return 1;
        }
        finally
        {
            await timeout.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
        }
    }
}
