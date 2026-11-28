namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyExpiredAuthenticatedCallDoesNotActivateTheService(string transport)
        => WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: false,
            VerifyNStudyAuthenticationExpiryAsync,
            configureServer: builder => builder.UseAuthenticator(SharpLinkAuthenticator.CreateServer((request, _) =>
                ValueTask.FromResult(request.Payload.Span.SequenceEqual(NStudyAuthenticationPayload)
                    ? SharpLinkAuthenticationResult.Authenticate(new SharpLinkAuthenticationContext(
                        "n-validation", expiresAt: DateTimeOffset.UtcNow.AddSeconds(3)))
                    : SharpLinkAuthenticationResult.Reject()))));

    private static async Task VerifyNStudyAuthenticationExpiryAsync(NStudyCoordinationFixture fixture,
        NStudyTrace trace, CancellationToken ct)
    {
        var (server, contracts, _, _, originalType, _, first, _, createClient) = fixture;
        var (client, session, peer, binding, b, receiver, _) = first;
        var identity = peer.AuthenticationContext ?? throw new InvalidOperationException("Expected an authenticated peer.");
        Ensure(identity.ExpiresAt.HasValue && !identity.IsExpired() && NStudyCount(originalType, "Created") == 0,
            "ordinary Prepare succeeds with a valid credential and no business activation");
        var observe = new NStudyDelayedNoticeCounter(binding.Contract);
        server.ReplaceInterceptors([observe]);
        trace.Record("BoundWhileCredentialIsValidWithoutServiceActivation", binding, b);

        await WaitForNStudyAsync(() => identity.IsExpired(), ct);
        try
        {
            _ = await InvokeValueTaskAsync<int>(receiver.Service, contracts.ContractType, "UnaryAsync", 4, ct);
            throw new InvalidOperationException("Expired authenticated call must not succeed.");
        }
        catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.AuthenticationExpired) { }

        var count = NStudyCount(originalType, "Created");
        Ensure(count == 0 && observe.Calls == 0 &&
               session.CaptureNStudyBinding(b.Contract) == b.BindingId &&
               ReferenceEquals(NStudyClientSession(client), session),
            $"credential expiry must reject before creating the original service or running an interceptor (created={count})");
        trace.Record("ExpiredAuthenticatedCallRefusesBeforeCreatingTheOriginalService", binding, b);

        await using var fresh = createClient();
        Ensure(fresh.RegisterAssembly(contracts.ContractAssembly).Succeeded, "the same local contract is available on a fresh connection");
        await fresh.ConnectAsync(ct);
        var (sibling, siblingBind) = await PrepareNStudySiblingAsync(fresh, b.Contract, ct);
        var ready = CreateNStudyExactGenerated(fresh, contracts.ContractType);
        var freshBind = await ready.PrepareExact(new(siblingBind.Authority, binding.Contract, binding.Hash, binding.Target),
            TimeSpan.FromSeconds(5), ct);
        Ensure(freshBind.Authority != binding.Authority && freshBind.Target == binding.Target &&
               await InvokeValueTaskAsync<int>(ready.Service, contracts.ContractType, "UnaryAsync", 5, ct) == 6 &&
               await sibling.TouchAsync(5, ct) == 105 &&
               NStudyCount(originalType, "Created") == 1 && observe.Calls == 1,
            "a newly authenticated normal Session with its own Prepare is the only one to create the original service");
        trace.Record("FreshCredentialAllowsNormalPreparedBusiness", freshBind, siblingBind);
    }
}
