namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    private ulong _nStudyInventoryIssued;
    private ulong _nStudyInventoryReceived;
    private Guid _nStudyInventoryPublisher;
    private long _nStudyInventoryRevision = -1;
    private PendingNStudyInventory? _nStudyInventory;

    internal bool HasNStudyAuthority(NStudyAuthority authority)
    {
        lock (_nStudyGate)
            return _nStudyAuthority != default && _nStudyAuthority == authority;
    }

    internal SharpLinkVersionSession? ReadObservedNStudyVersionSession()
    {
        lock (_nStudyGate)
            return _nStudyAuthority == default ? null : NStudyInventorySnapshot.ToVersionSession(_nStudyAuthority);
    }

    internal Task<NStudyInventorySnapshot> QueryNStudyInventoryAsync(CancellationToken cancellationToken)
        => QueryNStudyPeerInventoryAsync(RpcSessionRole.Client, null, cancellationToken);

    internal Task<NStudyInventorySnapshot> QueryNStudyClientInventoryAsync(NStudyAuthority authority, CancellationToken cancellationToken)
        => QueryNStudyPeerInventoryAsync(RpcSessionRole.Server, authority, cancellationToken);

    private async Task<NStudyInventorySnapshot> QueryNStudyPeerInventoryAsync(RpcSessionRole role,
        NStudyAuthority? authority, CancellationToken cancellationToken)
    {
        EnsureNStudyReady(role);
        cancellationToken.ThrowIfCancellationRequested();
        using var timer = new CancellationTokenSource(NStudyTimeout, RuntimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(LifetimeToken, timer.Token, cancellationToken);
        var ct = budget.Token;
        PendingNStudyInventory pending;
        lock (_nStudyGate)
        {
            if (_nStudyInventory is not null)
                throw new SharpLinkException(SharpLinkErrorCode.ResourceExhausted, "A private inventory query is already pending.");
            pending = new(checked(++_nStudyInventoryIssued), authority);
            _nStudyInventory = pending;
        }
        try
        {
            await SendNStudyInventoryFrameAsync(NStudyWire.InventoryQuery, pending.Operation, null, authority, ct)
                .ConfigureAwait(false);
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
        EnsureNStudyReady(Role);
        PendingNStudyInventory? pending;
        lock (_nStudyGate)
        {
            if (operation > _nStudyInventoryIssued)
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
            var authority = pending!.Authority ?? _nStudyAuthority;
            if ((authority != default && authority != snapshot.Authority) ||
                (Role == RpcSessionRole.Client && snapshot.PublisherEpoch != snapshot.Authority.RegistryEpoch))
                throw InvalidNStudy("Private inventory did not name the current physical Session and publisher.");
            if (snapshot.Status == NStudyInventoryStatus.Current)
            {
                if ((_nStudyInventoryPublisher != default && _nStudyInventoryPublisher != snapshot.PublisherEpoch) ||
                    snapshot.Revision < _nStudyInventoryRevision)
                    throw InvalidNStudy("Private inventory changed publisher or regressed revision on this Session.");
                if (Role == RpcSessionRole.Client)
                    _nStudyAuthority = snapshot.Authority;
                _nStudyInventoryPublisher = snapshot.PublisherEpoch;
                _nStudyInventoryRevision = snapshot.Revision;
            }
            pending.Completion.TrySetResult(snapshot);
        }
    }

    internal void ValidateNStudyInventoryQueryAuthority(NStudyAuthority authority)
    {
        EnsureNStudyReady(RpcSessionRole.Client);
        lock (_nStudyGate)
        {
            if (_nStudyAuthority != default && _nStudyAuthority != authority)
                throw InvalidNStudy("Private client inventory query named another physical Session.");
        }
    }

    internal ValueTask SendNStudyInventoryReplyAsync(ulong operation, NStudyInventorySnapshot snapshot, CancellationToken ct)
    {
        EnsureNStudyReady(Role);
        // Only the receive loop answers a query; this sequence is independent of outgoing queries.
        if (operation <= _nStudyInventoryReceived)
            throw InvalidNStudy("Private inventory query did not advance.");
        _nStudyInventoryReceived = operation;
        return SendNStudyInventoryFrameAsync(NStudyWire.InventoryReply, operation, snapshot, null, ct);
    }

    private async ValueTask SendNStudyInventoryFrameAsync(ProtocolV2FrameType type, ulong operation,
        NStudyInventorySnapshot? snapshot, NStudyAuthority? queryAuthority, CancellationToken ct)
    {
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(type, ProtocolV2FrameFlags.None, operation))
            {
                if (snapshot is not null)
                    NStudyWire.WriteInventory(writer, snapshot);
                else if (queryAuthority is { } authority)
                    NStudyWire.WriteInventoryQueryAuthority(writer, authority);
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

    private sealed class PendingNStudyInventory(ulong operation, NStudyAuthority? authority)
    {
        internal ulong Operation { get; } = operation;
        internal NStudyAuthority? Authority { get; } = authority;
        internal TaskCompletionSource<NStudyInventorySnapshot> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
