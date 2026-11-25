namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public async Task NStudyVersionSelectionsDoNotFollowAnotherTenantOrARefreshedSession(string transport)
    {
        const string tenantA = "tenant-a";
        const string tenantB = "tenant-b";
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = guard.Token;
        var trace = new NStudyTrace(transport);
        var builder = SharpLinkServerBuilder.Create()
            .UseAuthenticator(SharpLinkAuthenticator.CreateServer(static (request, _) =>
            {
                var tenant = System.Text.Encoding.UTF8.GetString(request.Payload.Span);
                return ValueTask.FromResult(tenant is tenantA or tenantB
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext(tenant, tenantId: tenant))
                    : SharpLinkAuthenticationResult.Reject());
            }))
            .RequireAuthentication();
        var memory = transport == "shared-memory" ? $"sharplink-n-tenants-{Guid.NewGuid():N}" : null;
        if (memory is null)
            builder.UseTcp(0, IPAddress.Loopback);
        else
            builder.UseSharedMemory(memory);
        var port = memory is null ? ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port : 0;

        await using var server = builder.Build();
        await using var first = BuildClient(tenantA);
        await using var second = BuildClient(tenantB);
        await server.StartAsync(ct);
        await Task.WhenAll(first.ConnectAsync(ct).AsTask(), second.ConnectAsync(ct).AsTask());
        var firstProxy = first.CreateProxy<IConnectionBehaviorService>();
        var secondProxy = second.CreateProxy<IConnectionBehaviorService>();
        await firstProxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        await secondProxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        var contractType = typeof(IConnectionBehaviorService);
        Ensure(SharpLinkAssemblyManifestLoader.TryLoad(contractType.Assembly, out var manifest).Succeeded,
            "read the actual generated service contract");
        var contract = manifest!.Contracts.Single(entry => entry.ContractType == contractType).ContractId;
        var firstSession = NStudyClientSession((SharpLinkClient)first);
        var secondSession = NStudyClientSession((SharpLinkClient)second);
        var firstRoute = firstSession.CaptureNStudyRoute(contract);
        var secondRoute = secondSession.CaptureNStudyRoute(contract);
        var firstSelector = first.GetVersionConnections().Single();
        var secondSelector = second.GetVersionConnections().Single();
        var firstInventory = await first.QueryPeerVersionInventoryAsync(firstSelector, ct);
        var secondInventory = await second.QueryPeerVersionInventoryAsync(secondSelector, ct);
        Ensure(firstInventory.Session is { } firstIdentity && secondInventory.Session is { } secondIdentity &&
               firstIdentity.ServerInstanceId == secondIdentity.ServerInstanceId &&
               firstIdentity.ServerRegistryEpoch == secondIdentity.ServerRegistryEpoch &&
               firstIdentity.PhysicalSessionId != secondIdentity.PhysicalSessionId &&
               firstRoute.Token == secondRoute.Token && firstRoute.Target == secondRoute.Target &&
               firstRoute.IsOpenOn(firstSession) && secondRoute.IsOpenOn(secondSession) &&
               !firstRoute.IsOpenOn(secondSession) && !secondRoute.IsOpenOn(firstSession),
            "matching numeric tokens select actual routes only on their original physical sessions");
        await ExpectAsync(first.QueryPeerVersionInventoryAsync(secondSelector, ct).AsTask(), SharpLinkErrorCode.Unavailable);
        await ExpectAsync(second.QueryPeerVersionInventoryAsync(firstSelector, ct).AsTask(), SharpLinkErrorCode.Unavailable);
        Ensure(await firstProxy.Service.RequireTenantAsync(tenantA) == 1 &&
               await secondProxy.Service.RequireTenantAsync(tenantB) == 1,
            "the same service authorizes each request using its own session's authenticated tenant");
        await ExpectAsync(secondProxy.Service.RequireTenantAsync(tenantA).AsTask(), SharpLinkErrorCode.AuthorizationDenied);
        trace.Record("BothTenantsRetainTheirOwnSelectorAndBinding", firstRoute.Acknowledgement, secondRoute.Acknowledgement);

        await server.PublishDesiredSessionAsync(new() { MaxFramePayloadBytes = 128 * 1024 },
            SharpLinkSessionRolloutMode.RollingRefresh, ct);
        await WaitForNStudyAsync(() => first.GetVersionConnections() is [var a] && a.TryGetConnection(out var ac) &&
            second.GetVersionConnections() is [var b] && b.TryGetConnection(out var bc) &&
            !ReferenceEquals(ac.Session, firstSession) && !ReferenceEquals(bc.Session, secondSession), ct);
        await ExpectAsync(first.QueryPeerVersionInventoryAsync(firstSelector, ct).AsTask(), SharpLinkErrorCode.Unavailable);
        await ExpectAsync(second.QueryPeerVersionInventoryAsync(secondSelector, ct).AsTask(), SharpLinkErrorCode.Unavailable);
        var freshA = NStudyClientSession((SharpLinkClient)first);
        var freshB = NStudyClientSession((SharpLinkClient)second);
        Ensure(freshA.NStudyPreparedCount == 0 && freshB.NStudyPreparedCount == 0 &&
               !firstRoute.IsOpenOn(freshA) && !secondRoute.IsOpenOn(freshB),
            "ordinary refresh leaves both fresh sessions without a prepared grant");
        await firstProxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        await secondProxy.PrepareAsync(TimeSpan.FromSeconds(5), ct);
        Ensure(await firstProxy.Service.RequireTenantAsync(tenantA) == 1 &&
               await secondProxy.Service.RequireTenantAsync(tenantB) == 1,
            "explicit Prepare restores each service on its newly authenticated connection");
        trace.Record("RefreshedTenantsMustPrepareTheirOwnFreshBinding",
            freshA.CaptureNStudyRoute(contract).Acknowledgement, freshB.CaptureNStudyRoute(contract).Acknowledgement);

        ISharpLinkClient BuildClient(string tenant)
        {
            var payload = System.Text.Encoding.UTF8.GetBytes(tenant);
            var client = SharpClientBuilder.Create().UseRequestTimeout(TimeSpan.FromSeconds(5))
                .UseAuthenticator(SharpLinkAuthenticator.CreateClient(_ => ValueTask.FromResult<ReadOnlyMemory<byte>>(payload)));
            return (memory is null ? client.UseTcp(IPAddress.Loopback.ToString(), port) : client.UseSharedMemory(memory)).Build();
        }

        async Task ExpectAsync(Task operation, SharpLinkErrorCode code)
        {
            try { await operation.WaitAsync(ct); }
            catch (SharpLinkException error) when (error.Code == code) { return; }
            throw new InvalidOperationException($"Expected {code} from the public operation.");
        }
    }
}
