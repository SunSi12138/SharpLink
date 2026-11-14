using DemoBase;
using SeparatedContracts;
using SharpLink.Runtime;
using SharpLink.Sdk;

[assembly: SharpLinkRpcContracts(typeof(IGreetingService))]
[assembly: SharpLinkClusterContractAssembly("greetings", typeof(IGreetingService))]

const int port = 19110;

using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
var client = DemoTcp.CreateClient(port);

try
{
    await DemoTcp.EnsureConnectedAsync(client, cts.Token, "Failed to connect to separated server.");
    var serviceProxy = client.CreateProxy<IGreetingService>();
    await serviceProxy.PrepareAsync(TimeSpan.FromSeconds(10), cts.Token);
    var service = serviceProxy.Service;

    var greet = await service.Greet(new GreetRequest
    {
        Name = "SharpLink",
        Repeat = 3
    });
    Console.WriteLine($"Greet: {greet}");

    var sum = await service.Add(12, 30);
    Console.WriteLine($"Add: {sum}");
}
finally
{
    await client.DisposeAsync();
}
