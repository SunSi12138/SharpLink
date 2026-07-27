using System.Text;
using DemoBase;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

const string token = "demo-token";
var port = DemoStream.GetFreePort();
using var app = new CancellationTokenSource(TimeSpan.FromSeconds(15));

var server = DemoTcp.CreateServer<ISecureService, SecureService>(port, builder => builder
    .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) =>
    {
        var supplied = Encoding.UTF8.GetString(request.Payload.Span);
        var result = supplied == token
            ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext(
                subject: "demo-user",
                tenantId: "demo-tenant",
                scopes: ["demo.read"],
                expiresAt: DateTimeOffset.UtcNow.AddMinutes(5)))
            : SharpLinkAuthenticationResult.Reject();
        return ValueTask.FromResult(result);
    }))
    .RequireAuthentication());
var serverTask = DemoTcp.StartServerAsync(server, app.Token);

var client = DemoTcp.CreateClient(port, builder => builder.UseAuthenticator(
    SharpLinkAuthenticator.CreateClient(_ =>
        ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(token)))));

try
{
    await DemoTcp.EnsureConnectedAsync(client, app.Token);
    var identity = await client.Get<ISecureService>().WhoAmIAsync(app.Token);
    Console.WriteLine(identity);
}
finally
{
    await DemoTcp.ShutdownAsync(app, serverTask, client, server);
}

[RpcContract]
public interface ISecureService : IService
{
    ValueTask<string> WhoAmIAsync(CancellationToken cancellationToken);
}

[RpcService]
public sealed class SecureService : ISecureService
{
    public ValueTask<string> WhoAmIAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var identity = SharpLinkAuthorization.RequireActiveToken();
        SharpLinkAuthorization.RequireScope("demo.read");
        SharpLinkAuthorization.RequireTenant("demo-tenant");
        return ValueTask.FromResult($"authenticated subject={identity.Subject}, tenant={identity.TenantId}");
    }
}
