namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private ulong _nStudyInventorySequence;
    private long _nStudyInventoryRevision = -1;
    private PendingNStudyInventory? _nStudyInventory;

    internal bool HasNStudyAuthority(NStudyAuthority authority)
    {
        lock (_nStudyGate)
            return _nStudyAuthority != default && _nStudyAuthority == authority;
    }

    internal async Task<NStudyInventorySnapshot> QueryNStudyInventoryAsync(CancellationToken cancellationToken)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        cancellationToken.ThrowIfCancellationRequested();
        using var timer = new CancellationTokenSource(NStudyTimeout, RuntimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, timer.Token, cancellationToken);
        var ct = budget.Token;
        PendingNStudyInventory pending;
        lock (_nStudyGate)
        {
            if (_nStudyInventory is not null)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "A private inventory query is already pending.");
            pending = new(checked(++_nStudyInventorySequence));
            _nStudyInventory = pending;
        }
        try
        {
            await SendNStudyInventoryFrameAsync(NStudyWire.InventoryQuery, pending.Operation, null, ct).ConfigureAwait(false);
            var snapshot = await pending.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return snapshot;
        }
        catch (OperationCanceledException) when (timer.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, "The private inventory query expired.");
        }
        finally
        {
            // A read-only request owns no remote binding. A late reply can simply be ignored.
            lock (_nStudyGate)
                _nStudyInventory = null;
        }
    }

    internal void OnNStudyInventoryReply(ulong operation, ReadOnlySequence<byte> payload)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        PendingNStudyInventory? pending;
        lock (_nStudyGate)
        {
            if (operation > _nStudyInventorySequence)
                throw InvalidNStudy("Inventory reply does not match an issued query.");
            pending = _nStudyInventory;
            if (pending?.Operation != operation)
                return;
        }
        var snapshot = NStudyWire.ReadInventory(payload);
        lock (_nStudyGate)
        {
            if (!ReferenceEquals(_nStudyInventory, pending))
                return;
            if (snapshot.Status == NStudyInventoryStatus.Current)
            {
                if ((_nStudyAuthority != default && _nStudyAuthority != snapshot.Authority) ||
                    snapshot.Revision < _nStudyInventoryRevision)
                    throw InvalidNStudy("Private inventory changed authority or revision on this Session.");
                _nStudyAuthority = snapshot.Authority;
                _nStudyInventoryRevision = snapshot.Revision;
            }
            pending!.Completion.TrySetResult(snapshot);
        }
    }

    internal ValueTask SendNStudyInventoryReplyAsync(ulong operation, NStudyInventorySnapshot snapshot, CancellationToken ct)
    {
        EnsureNStudyReady(RpcSessionRole.Server);
        if (operation <= _nStudyInventorySequence)
            throw InvalidNStudy("Private inventory query did not advance.");
        _nStudyInventorySequence = operation;
        return SendNStudyInventoryFrameAsync(NStudyWire.InventoryReply, operation, snapshot, ct);
    }

    private async ValueTask SendNStudyInventoryFrameAsync(ProtocolV2FrameType type, ulong operation,
        NStudyInventorySnapshot? snapshot, CancellationToken ct)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, operation))
            {
                if (snapshot is not null)
                    NStudyWire.WriteInventory(writer, snapshot);
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

    private sealed class PendingNStudyInventory(ulong operation)
    {
        internal ulong Operation { get; } = operation;
        internal TaskCompletionSource<NStudyInventorySnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
