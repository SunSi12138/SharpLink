namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal SharpLinkVersionInventory ReadLocalVersionInventory()
        => _assemblyRegistry.CaptureNStudyInventory(default, int.MaxValue).ToVersionInventory(remote: false);

    private async ValueTask HandleNStudyInventoryAsync(RpcSession session, ulong operation,
        NStudyAuthority authority, CancellationToken ct)
    {
        session.ValidateNStudyInventoryQueryAuthority(authority);
        var snapshot = _assemblyRegistry.CaptureNStudyInventory(authority,
            NStudyWire.GetInventoryCapacity(session.NegotiatedMaxFramePayloadBytes));
        using var timer = new CancellationTokenSource(RpcSession.NStudyTimeout, _runtimeContext.TimeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        await session.SendNStudyInventoryReplyAsync(operation, snapshot, budget.Token).ConfigureAwait(false);
    }
}
