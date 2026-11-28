using System.Net;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.PackageAotSmoke;

[RpcService]
public sealed class PackageAotService : IPackageAotContract
{
    public ValueTask<PackageAotPayload> EchoAsync(PackageAotPayload value) => ValueTask.FromResult(value);
}

internal static class NativeVersionInventoryProbe
{
    private static readonly byte[] s_credential = [17, 25, 46];

    internal static async Task RunAsync(string transport)
    {
        Require(!RuntimeFeature.IsDynamicCodeSupported, "Run the published NativeAOT executable.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = deadline.Token;
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (request, _) => ValueTask.FromResult(
                request.Payload.Span.SequenceEqual(s_credential)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext("package-aot"))
                    : SharpLinkAuthenticationResult.Reject())))
            .RequireAuthentication();
        var clientBuilder = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(5))
            .UseAuthenticator(SharpLinkAuthenticator.CreateClient(static _ =>
                ValueTask.FromResult<ReadOnlyMemory<byte>>(s_credential)));
        if (transport == "tcp")
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback);
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port);
        }
        else
        {
            var name = $"sl-package-aot-{Guid.NewGuid():N}";
            serverBuilder.UseSharedMemory(name);
            clientBuilder.UseSharedMemory(name);
        }

        await using var server = serverBuilder.Build();
        await using var client = clientBuilder.Build();
        server.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
        client.SetVersionCoordinationMode(SharpLinkVersionCoordinationMode.Automatic);
        await server.StartAsync(ct);
        await client.ConnectAsync(ct);

        var manifest = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot().Single(static entry =>
            entry.Contracts.Any(static contract => contract.ContractType == typeof(IPackageAotContract)));
        var contract = manifest.Contracts.Single(static entry => entry.ContractType == typeof(IPackageAotContract));
        var localServer = server.GetLocalVersionInventory();
        var localClient = client.GetLocalVersionInventory();
        Require(localServer.Registrations.Single(item => item.ContractId == contract.ContractId).AssemblyHash == manifest.RpcAssemblyHash &&
                localClient.Registrations.Single(item => item.ContractId == contract.ContractId).AssemblyHash == manifest.RpcAssemblyHash,
            "The generated contract must be published at both packaged endpoints.");
        var initialServerView = await client.QueryPeerVersionInventoryAsync(ct);
        var peer = server.GetVersionPeers().Single();
        Require(peer.PhysicalSessionId != Guid.Empty, "The authenticated physical Session must be populated.");
        VerifyInventory(localServer, initialServerView, peer, "server");
        VerifyInventory(localClient, await server.QueryPeerVersionInventoryAsync(peer, ct), peer, "client");

        var proxy = client.CreateProxy<IPackageAotContract>();
        await proxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        await VerifyBusinessAsync(42);
        try
        {
            _ = await client.DeclareVersionTargetAsync(client.GetLocalContract<IPackageAotContract>(), ct);
            throw new InvalidOperationException("A linked static contract was accepted as a dynamic target.");
        }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.FailedPrecondition)
        {
            // The static contract remains callable; there is no installed dynamic registration.
        }
        var clientRegistration = client.RegisterAssembly(typeof(IPackageAotContract).Assembly);
        var serverRegistration = server.RegisterAssembly(typeof(IPackageAotContract).Assembly);
        Require(!clientRegistration.Succeeded && !serverRegistration.Succeeded &&
                clientRegistration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported &&
                serverRegistration.Error?.Code == SharpLinkAssemblyRegistrationErrorCode.PlatformNotSupported,
            "The packaged NativeAOT endpoints must reject unsupported runtime assembly registration.");
        await VerifyPeerInventoriesAsync();
        await VerifyBusinessAsync(43);
        Require(server.GetVersionPeers().Single() == peer, "The original authenticated connection must still be in use.");
        Console.WriteLine($"PACKAGE_AOT_VERSION_INVENTORY_PASS transport={transport}");

        async Task VerifyPeerInventoriesAsync()
        {
            VerifyInventory(localServer, await client.QueryPeerVersionInventoryAsync(ct), peer, "server");
            VerifyInventory(localClient, await server.QueryPeerVersionInventoryAsync(peer, ct), peer, "client");
        }

        async Task VerifyBusinessAsync(int count)
        {
            var expected = new PackageAotPayload { Count = count, Stamp = 0x0102030405060708 };
            var response = await proxy.Service.EchoAsync(expected);
            Require(response.Count == expected.Count && response.Stamp == expected.Stamp,
                "The original prepared static RPC must round-trip normal data.");
        }
    }

    private static void VerifyInventory(SharpLinkVersionInventory local, SharpLinkVersionInventory remote,
        SharpLinkVersionSession peer, string direction) =>
        Require(local.Session is null && remote.Session == peer && local.PublisherEpoch != Guid.Empty &&
                remote.PublisherEpoch == local.PublisherEpoch && remote.Revision == local.Revision &&
                remote.Registrations.SequenceEqual(local.Registrations),
            $"The {direction} peer view must match its local publication on the original Session.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
