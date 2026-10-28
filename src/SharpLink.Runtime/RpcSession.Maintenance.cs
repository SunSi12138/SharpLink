namespace SharpLink.Runtime;

internal sealed partial class RpcSession
{
    // Cold-only transport fact: configuration cannot turn an unauthenticated raw transport into
    // a trusted maintenance authority. Existing TLS certificate validation remains authoritative.
    internal bool HasAuthenticatedTransport => _transport is ITransportSecurityInfo security &&
        security.Protocol != System.Security.Authentication.SslProtocols.None;

    // No maintenance field is placed on ordinary session instances. The weak cold owner is
    // allocated only for explicitly enrolled sessions and never queried by normal RPC methods.
    private static class MaintenanceStorage
    {
        internal static ConditionalWeakTable<RpcSession, MaintenanceEnrollmentBox>? Enrollments;
    }

    internal CooperativeMaintenanceMessage? MaintenanceEnrollment
    {
        get => Volatile.Read(ref MaintenanceStorage.Enrollments)?.TryGetValue(this, out var box) == true ? box.Message : null;
        set
        {
            if (value is { } message)
                LazyInitializer.EnsureInitialized(ref MaintenanceStorage.Enrollments).AddOrUpdate(this, new MaintenanceEnrollmentBox(message));
            else
                Volatile.Read(ref MaintenanceStorage.Enrollments)?.Remove(this);
        }
    }

    private sealed class MaintenanceEnrollmentBox(CooperativeMaintenanceMessage message)
    {
        internal readonly CooperativeMaintenanceMessage Message = message;
    }

    internal void MarkDraining()
    {
        while (true)
        {
            var current = Volatile.Read(ref _protocolState);
            if (current.Phase is not (RpcSessionProtocolPhase.Ready or RpcSessionProtocolPhase.MaintenancePaused))
                return;
            var draining = current.WithPhase(RpcSessionProtocolPhase.Draining);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _protocolState, draining, current), current))
                return;
        }
    }

    internal void PauseForMaintenance()
    {
        TransitionProtocolPhase(RpcSessionProtocolPhase.Ready, RpcSessionProtocolPhase.MaintenancePaused);
        if (ProtocolPhase != RpcSessionProtocolPhase.MaintenancePaused || !IsConnected)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Session cannot enter maintenance.");
    }

    internal void ResumeFromMaintenance()
    {
        TransitionProtocolPhase(RpcSessionProtocolPhase.MaintenancePaused, RpcSessionProtocolPhase.Ready);
        if (ProtocolPhase != RpcSessionProtocolPhase.Ready || !IsConnected)
            throw new SharpLinkException(SharpLinkErrorCode.Unavailable, "Session cannot resume maintenance.");
    }

    internal async ValueTask SendMaintenanceAsync(
        CooperativeMaintenanceMessage message,
        CancellationToken cancellationToken = default)
    {
        if ((NegotiatedCapabilities & ProtocolV2Capabilities.CooperativeMaintenance) == 0)
            throw ProtocolV2FrameParser.Violation("Maintenance capability was not negotiated.");
        var writer = RentFrameWriter();
        var ownsWriter = true;
        try
        {
            using (writer.BeginPacketScope(ProtocolV2FrameType.CooperativeMaintenance,
                       ProtocolV2FrameFlags.None, 0))
                message.Write(writer);
            ownsWriter = false;
            // CooperativeMaintenance is intentionally absent from IsProtocolProgressFrame.
            // A forced flush cannot let this delimiter jump over older normal payloads.
            await SendPacketAndFlushAsync(writer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (ownsWriter)
                RuntimeContext.Buffers.Return(writer);
        }
    }
}
