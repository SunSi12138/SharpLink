using QuickStart.Contracts;
using SharpLink.Client;
using SharpLink.Sdk;

[assembly: SharpLinkRpcContracts(typeof(IGreetingService))]

const int port = 50051;
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
await using var client = SharpClientBuilder.Create()
    .UseRequestTimeout(TimeSpan.FromSeconds(5))
    .UseTcp("127.0.0.1", port)
    .Build();

await client.ConnectAsync(timeout.Token);
await client.WaitForReadinessAsync(1, timeout.Token);

var greeting = client.Get<IGreetingService>();
var reply = await greeting.GreetAsync(
    new GreetingRequest { Name = "SharpLink" },
    timeout.Token);

if (!string.Equals(reply.Message, "Hello, SharpLink!", StringComparison.Ordinal))
    throw new InvalidOperationException($"Unexpected Quick Start response: '{reply.Message}'.");

Console.WriteLine($"QUICKSTART_CLIENT_PASS response={reply.Message}");
await client.StopAsync();
