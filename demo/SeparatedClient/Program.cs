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
    var proxy = client.CreateProxy<IGreetingService>();
    await client.ConnectAsync(cts.Token);
    await proxy.PrepareAsync(TimeSpan.FromSeconds(5), cts.Token);
    var service = proxy.Service;

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
