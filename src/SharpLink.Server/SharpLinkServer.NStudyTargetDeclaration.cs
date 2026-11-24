namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private async ValueTask HandleNStudyTargetDeclarationAsync(ServerConnectionState connection, ulong operation,
        NStudyTargetDeclaration declaration, CancellationToken ct)
    {
        var session = connection.Session;
        session.AcceptNStudyTargetDeclarationOperation(operation);
        var reply = ObserveNStudyDeclaredTarget(connection, declaration);
        using var timer = new CancellationTokenSource(RpcSession.NStudyTimeout, _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        await session.SendNStudyTargetDeclarationReplyAsync(operation, reply, budget.Token).ConfigureAwait(false);
    }

    private NStudyTargetDeclarationReply ObserveNStudyDeclaredTarget(ServerConnectionState connection,
        NStudyTargetDeclaration declaration)
    {
        var authority = ReadNStudySessionAuthority(connection);
        var reply = new NStudyTargetDeclarationReply(declaration, authority, NStudyTargetStatus.NotReady, default);
        lock (_registryGate)
        {
            if (CurrentState != ServerState.Running || connection.LifecycleState != ServerConnectionLifecycleState.Ready ||
                connection.AuthenticationContext is not { } auth || auth.IsExpired())
                return reply;

            var mode = NStudyVersionCoordinationMode;
            if (mode == NStudyCoordinationMode.Disabled)
                return reply with { Status = NStudyTargetStatus.Disabled };
            if (!Volatile.Read(ref _services).TryGetValue(declaration.Contract, out var service))
                return reply with { Status = NStudyTargetStatus.Missing };
            if (service.NStudyContractHash != declaration.Hash)
                return reply with { Status = NStudyTargetStatus.Incompatible };
            if (!service.AcceptsCalls || !connection.Session.MayBindNStudyTarget(service.NStudyTarget))
                return reply;
            if (mode != NStudyCoordinationMode.Automatic)
                return reply with { Status = NStudyTargetStatus.ApprovalRequired };

            // Observation only: never Install, Stage, open admission, or run an application callback.
            // An exact ordinary Bind still has to check this publication on the same Session.
            return reply with { Status = NStudyTargetStatus.Observed, Target = service.NStudyTarget };
        }
    }
}
