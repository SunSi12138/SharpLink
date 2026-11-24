namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal async Task<NStudyTargetDeclarationReply> DeclareNStudyTargetAsync(Type localContract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(localContract);
        RequireNStudyTargetDeclarationsEnabled(ct);
        if (!_assemblyRegistry.CaptureProxySnapshot().TryGetValue(localContract, out var registration))
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "No actual local dynamic contract for the declaration.");
        return await DeclareNStudyTargetCoreAsync(registration, ct).ConfigureAwait(false);
    }

    internal async ValueTask<SharpLinkVersionDeclarationResult> DeclareVersionTargetAsync(
        RpcLocalContract localContract, CancellationToken ct, SharpLinkVersionConnection? selector = null)
    {
        ArgumentNullException.ThrowIfNull(localContract);
        RequireNStudyTargetDeclarationsEnabled(ct);
        var registration = _assemblyRegistry.ResolveNStudyLocalContract(localContract);
        return NStudyVersionProjection.ToDeclaration(await DeclareNStudyTargetCoreAsync(registration, ct, selector).ConfigureAwait(false));
    }

    private async Task<NStudyTargetDeclarationReply> DeclareNStudyTargetCoreAsync(
        ClientProxyRegistration registration, CancellationToken ct, SharpLinkVersionConnection? selector = null)
    {
        if (registration.Module is not { } module || !module.TryAcquire(stream: false, out var use))
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "No actual local dynamic contract for the declaration.");

        using (use)
        {
            var connection = selector is null ? RequireSingleNStudyConnection() : RequireVersionConnection(selector);
            var declaration = new NStudyTargetDeclaration(registration.Descriptor.ContractId, registration.NStudyAssemblyHash,
                new(module.NStudyRegistration, module.NStudyPublication));
            var reply = await connection.Session.DeclareNStudyTargetAsync(declaration, ct).ConfigureAwait(false);
            if (!IsNStudyPreparationConnectionCurrent(connection) ||
                !_assemblyRegistry.CaptureProxySnapshot().TryGetValue(registration.Descriptor.ContractType, out var current) ||
                !ReferenceEquals(registration, current) || module.State != SharpLinkDynamicModuleState.Running)
                throw NStudyPreparedSite.Unavailable();
            return reply;
        }
    }

    private void RequireNStudyTargetDeclarationsEnabled(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (NStudyVersionCoordinationMode == NStudyCoordinationMode.Disabled)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Local policy has disabled target declarations.");
    }
}
