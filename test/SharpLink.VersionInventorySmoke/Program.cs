using System.Net;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

if (args is ["coordinate", ("tcp" or "shared-memory"), ("manual" or "automatic" or "cohort" or "follower-defer"), var plugins])
{
    await CoordinationSmoke.RunAsync(args[1], args[2], Path.GetFullPath(plugins));
    return;
}
if (args is not [("tcp" or "shared-memory")])
    throw new ArgumentException("Use <transport> or coordinate <transport> <mode> <plugin-directory>.");

var transport = args[0];
using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(25));
var ct = guard.Token;
var timeout = TimeSpan.FromSeconds(5);
var memoryName = transport == "shared-memory" ? $"sl-inventory-{Guid.NewGuid():N}" : null;
ReadOnlyMemory<byte> payload = "inventory-smoke"u8.ToArray();
var serverBuilder = SharpLinkServerBuilder.Create()
    .UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) => ValueTask.FromResult(
        request.Payload.Span.SequenceEqual(payload.Span)
            ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("inventory-smoke"))
            : SharpLinkAuthenticationResult.Reject())))
    .RequireAuthentication()
    .ReplaceService<IVersionInventoryProbe>(new VersionInventoryProbe());
if (memoryName is null)
    serverBuilder.UseTcp(0, IPAddress.Loopback);
else
    serverBuilder.UseSharedMemory(memoryName);
var port = memoryName is null ? ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port : 0;
await using var server = serverBuilder.Build();
var serverLocal = server.GetLocalVersionInventory();
Check(serverLocal.Session is null && serverLocal.PublisherEpoch != Guid.Empty &&
    server.GetVersionPeers().Count == 0, "local server inventory is available before Start without a peer");
await server.StartAsync(ct);

ISharpLinkClient CreateClient()
{
    var builder = SharpClientBuilder.Create().UseRequestTimeout()
        .UseConnectionPool(pool => { pool.MinConnections = 1; pool.MaxConnections = 1; })
        .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult(payload)));
    return (memoryName is null
        ? builder.UseTcp(IPAddress.Loopback.ToString(), port)
        : builder.UseSharedMemory(memoryName)).Build();
}

async Task<(SharpLinkVersionSession Session, Guid ClientEpoch)> ReadBothAndCallAsync(
    ISharpLinkClient client, int value)
{
    var local = client.GetLocalVersionInventory();
    var proxy = client.CreateProxy<IVersionInventoryProbe>();
    Check(local.Session is null && local.PublisherEpoch != Guid.Empty, "local client publisher exists before Connect");
    await client.ConnectAsync(ct);
    var forward = await client.QueryPeerVersionInventoryAsync(ct);
    var peer = forward.Session ?? throw new InvalidOperationException("No physical session for server inventory.");
    Check(server.GetVersionPeers().Contains(peer), "both applications see the same current physical peer");
    var reverse = await server.QueryPeerVersionInventoryAsync(peer, ct);
    Check(reverse.Session == peer &&
        peer.ServerInstanceId != Guid.Empty && peer.ServerRegistryEpoch == forward.PublisherEpoch &&
        peer.PhysicalSessionId != Guid.Empty && local.PublisherEpoch != forward.PublisherEpoch,
        "session authority and the two independent publishers are distinguishable");
    CheckSnapshot(serverLocal, forward);
    CheckSnapshot(local, reverse);
    var clientContract = local.Registrations.Single();
    var serviceContract = serverLocal.Registrations.Single();
    Check(clientContract.ContractId == serviceContract.ContractId &&
        clientContract.AssemblyHash == serviceContract.AssemblyHash &&
        !clientContract.AssemblyHash.IsEmpty &&
        clientContract.RegistrationId != Guid.Empty && serviceContract.RegistrationId != Guid.Empty &&
        clientContract.PublicationGeneration > 0 && serviceContract.PublicationGeneration > 0,
        "generated contract/hash and published registration facts describe the actual deployment");
    await proxy.PrepareAsync(timeout, ct);
    Check(await proxy.Service.IncrementAsync(value, ct) == value + 1, "prepared generated RPC on the queried connection");
    return (peer, local.PublisherEpoch);
}

SharpLinkVersionSession retired;
Guid previousClientEpoch;
await using (var first = CreateClient())
    (retired, previousClientEpoch) = await ReadBothAndCallAsync(first, 41);
while (server.GetVersionPeers().Contains(retired))
    await Task.Delay(TimeSpan.FromMilliseconds(20), ct);

await using var second = CreateClient();
var (current, clientEpoch) = await ReadBothAndCallAsync(second, 9);
Check(current.PhysicalSessionId != retired.PhysicalSessionId &&
    current.ServerInstanceId == retired.ServerInstanceId &&
    current.ServerRegistryEpoch == retired.ServerRegistryEpoch && clientEpoch != previousClientEpoch,
    "disconnect replaces the physical identity, not the server registry; new client owns its publisher");
try
{
    _ = await server.QueryPeerVersionInventoryAsync(retired, ct);
    throw new InvalidOperationException("The retired peer selected another connection.");
}
catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.Unavailable)
{
    Check((await server.QueryPeerVersionInventoryAsync(current, ct)).PublisherEpoch == clientEpoch,
        "rejecting a retired peer leaves the new one usable");
}
Console.WriteLine($"VERSION_INVENTORY_PASS transport={transport} bilateral=true generated=true reconnect=true");

static void CheckSnapshot(SharpLinkVersionInventory local, SharpLinkVersionInventory remote)
    => Check(local.PublisherEpoch == remote.PublisherEpoch && local.Revision == remote.Revision &&
        local.Registrations.SequenceEqual(remote.Registrations), "peer inventory matches the actual local publisher");

static void Check(bool condition, string description)
{
    if (!condition)
        throw new InvalidOperationException(description);
}

[RpcContract]
public interface IVersionInventoryProbe : IService
{
    ValueTask<int> IncrementAsync(int value, CancellationToken cancellationToken = default);
}

[RpcService]
public sealed class VersionInventoryProbe : IVersionInventoryProbe
{
    public ValueTask<int> IncrementAsync(int value, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(value + 1);
}
