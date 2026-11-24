namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private ulong _nStudyTargetDeclarationIssued;
    private ulong _nStudyTargetDeclarationReceived;
    private PendingNStudyTargetDeclaration? _nStudyTargetDeclaration;

    internal bool SupportsNStudyTargetDeclaration => SupportsNStudyCoordination &&
        (NegotiatedCapabilities & NStudyWire.TargetDeclarationCapability) != 0;

    internal async Task<NStudyTargetDeclarationReply> DeclareNStudyTargetAsync(
        NStudyTargetDeclaration declaration, CancellationToken cancellationToken)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        if (!SupportsNStudyTargetDeclaration)
            throw new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "The peer did not negotiate target declarations.");
        cancellationToken.ThrowIfCancellationRequested();
        using var timer = new CancellationTokenSource(NStudyTimeout, RuntimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, timer.Token, cancellationToken);
        var ct = budget.Token;
        PendingNStudyTargetDeclaration pending;
        lock (_nStudyGate)
        {
            if (_nStudyTargetDeclaration is not null)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "A target declaration is already pending.");
            pending = new(checked(++_nStudyTargetDeclarationIssued), declaration);
            _nStudyTargetDeclaration = pending;
        }
        try
        {
            await SendNStudyTargetFrameAsync(pending.Operation, declaration, null, ct).ConfigureAwait(false);
            var reply = await pending.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return reply;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested && !LifetimeToken.IsCancellationRequested)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "The target declaration expired.");
        }
        finally
        {
            // There is no remote grant to unwind. An expired request can ignore its late reply.
            lock (_nStudyGate)
                _nStudyTargetDeclaration = null;
        }
    }

    internal void OnNStudyTargetDeclarationReply(ulong operation, ReadOnlySequence<byte> payload)
    {
        ValidateNStudyTargetFrame(RpcSessionRole.Client);
        lock (_nStudyGate)
        {
            if (operation == 0 || operation > _nStudyTargetDeclarationIssued)
                throw InvalidNStudy("The target reply does not match an issued declaration.");
            if (_nStudyTargetDeclaration is not { } pending || operation != pending.Operation)
                return;

            var reply = NStudyWire.ReadTargetDeclarationReply(payload);
            if (reply.Declaration != pending.Declaration ||
                (_nStudyAuthority != default && _nStudyAuthority != reply.Authority))
                throw InvalidNStudy("The target reply changed the declaration or physical authority.");
            _nStudyAuthority = reply.Authority;
            pending.Completion.TrySetResult(reply);
        }
    }

    internal void AcceptNStudyTargetDeclarationOperation(ulong operation)
    {
        ValidateNStudyTargetFrame(RpcSessionRole.Server);
        if (operation <= _nStudyTargetDeclarationReceived)
            throw InvalidNStudy("The target declaration did not advance on this physical Session.");
        _nStudyTargetDeclarationReceived = operation;
    }

    internal ValueTask SendNStudyTargetDeclarationReplyAsync(
        ulong operation, NStudyTargetDeclarationReply reply, CancellationToken ct)
        => SendNStudyTargetFrameAsync(operation, reply.Declaration, reply, ct);

    private void ValidateNStudyTargetFrame(RpcSessionRole role)
    {
        EnsureNStudyReady(role);
        if (!SupportsNStudyTargetDeclaration)
            throw InvalidNStudy("Target declaration was not negotiated on this physical Session.");
    }

    private async ValueTask SendNStudyTargetFrameAsync(ulong operation, NStudyTargetDeclaration declaration,
        NStudyTargetDeclarationReply? response, CancellationToken ct)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            var type = response.HasValue ? NStudyWire.TargetDeclarationReply : NStudyWire.TargetDeclaration;
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, operation))
            {
                if (response is { } reply)
                    NStudyWire.WriteTargetDeclarationReply(writer, reply);
                else
                    NStudyWire.WriteTargetDeclaration(writer, declaration);
            }
            ownsWriter = false;
            await SendPacketAndFlushAsync(writer, ct).ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }

    private sealed class PendingNStudyTargetDeclaration(ulong operation, NStudyTargetDeclaration declaration)
    {
        internal ulong Operation { get; } = operation;
        internal NStudyTargetDeclaration Declaration { get; } = declaration;
        internal TaskCompletionSource<NStudyTargetDeclarationReply> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
