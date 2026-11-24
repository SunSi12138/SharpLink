namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal async Task<NStudyTargetDeclarationReply> DeclareNStudyTargetAsync(Type localContract, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(localContract);
        ct.ThrowIfCancellationRequested();
        if (NStudyVersionCoordinationMode == NStudyCoordinationMode.Disabled)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Local policy has disabled target declarations.");
        if (!_assemblyRegistry.CaptureProxySnapshot().TryGetValue(localContract, out var registration) ||
            registration.Module is not { } module || !module.TryAcquire(stream: false, out var use))
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "No actual local dynamic contract for the declaration.");

        using (use)
        {
            var connection = RequireSingleNStudyConnection();
            var declaration = new NStudyTargetDeclaration(registration.Descriptor.ContractId, registration.NStudyAssemblyHash,
                new(module.NStudyRegistration, module.NStudyPublication));
            var reply = await connection.Session.DeclareNStudyTargetAsync(declaration, ct).ConfigureAwait(false);
            if (!IsNStudyPreparationConnectionCurrent(connection) ||
                !_assemblyRegistry.CaptureProxySnapshot().TryGetValue(localContract, out var current) ||
                !ReferenceEquals(registration, current) || module.State != SharpLinkDynamicModuleState.Running)
                throw NStudyPreparedSite.Unavailable();
            return reply;
        }
    }
}
