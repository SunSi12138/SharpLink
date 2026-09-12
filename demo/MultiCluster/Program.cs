using System.Net;
using DemoBase;
using MultiCluster.Orders.Contracts;
using MultiCluster.Payments.Contracts;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

var ordersPort = DemoStream.GetFreePort();
var paymentsPort = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(20));

var ordersServer = CreateServer(ordersPort);
var paymentsServer = CreateServer(paymentsPort);
var ordersTask = DemoTcp.StartServerAsync(ordersServer, app.Token);
var paymentsTask = DemoTcp.StartServerAsync(paymentsServer, app.Token);

var client = SharpLinkMultiClusterClientBuilder.Create()
    .UseRequestTimeout()
    .AddCluster("orders", child => child.UseTcp(IPAddress.Loopback.ToString(), ordersPort))
    .AddCluster("payments", child => child.UseTcp(IPAddress.Loopback.ToString(), paymentsPort))
    .Build();

try
{
    await client.ConnectAsync(app.Token);
    var orders = await client.Get<IOrdersService>().GetClusterAsync(app.Token);
    var payments = await client.Get<IPaymentsService>().GetClusterAsync(app.Token);
    Console.WriteLine($"orders route -> {orders}");
    Console.WriteLine($"payments route -> {payments}");
    if (orders != "orders" || payments != "payments")
        throw new InvalidOperationException("A generated contract route selected the wrong cluster.");
}
finally
{
    app.Cancel();
    await client.DisposeAsync();
    await ordersServer.DisposeAsync();
    await paymentsServer.DisposeAsync();
    await Task.WhenAll(
        Task.WhenAny(ordersTask, Task.Delay(300)),
        Task.WhenAny(paymentsTask, Task.Delay(300)));
}

static ISharpLinkServer CreateServer(int port)
    => SharpLinkServerBuilder.Create()
        .UseTcp(port, IPAddress.Loopback.ToString())
        .Build();

[RpcService]
public sealed class OrdersService : IOrdersService
{
    public ValueTask<string> GetClusterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("orders");
    }
}

[RpcService]
public sealed class PaymentsService : IPaymentsService
{
    public ValueTask<string> GetClusterAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult("payments");
    }
}
