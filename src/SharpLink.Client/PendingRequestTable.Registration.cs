namespace SharpLink.Client;

internal sealed partial class PendingRequestTable
{
    private void OnRegistered(PendingCall call)
    {
        // A competing cancellation or connection close can take the published slot immediately
        // and wait in WaitUntilRegistered(). Always publish the registration barrier, even if the
        // owner/telemetry registration throws before the normal MarkRegistered boundary.
        try
        {
            try
            {
                SharpLinkTelemetry.AddPendingRequests(1);
                _owner.OnPendingCallRegistered();
            }
            finally
            {
                call.MarkRegistered();
            }
        }
        catch (Exception registrationException)
        {
            // The slot already owns the operation and observer. Complete it through the one
            // authoritative terminal path; a separate failed ValueTask would strand the slot
            // and keep graceful retirement waiting until connection shutdown.
            TryComplete(call.Id, PendingCallCompletionReason.SendFailure, registrationException);
            return;
        }

        if (call.Deadline.HasValue)
        {
            try
            {
                _deadlineScheduler.Observe(call.Deadline);
            }
            catch (Exception registrationException)
            {
                // MarkRegistered has already run, so this cannot wait for itself. A concurrent
                // cancellation/close may have claimed the slot first; TryComplete then returns
                // false and that winner remains authoritative.
                TryComplete(call.Id, PendingCallCompletionReason.SendFailure, registrationException);
                return;
            }
        }

        // Keep terminal owner/observer exceptions from this call visible: never catch
        // TryComplete itself or CompleteRegistrationIfDisposed as registration diagnostics.
        if (call.CancellationToken.IsCancellationRequested)
            TryComplete(call.Id, PendingCallCompletionReason.UserCancellation);
    }
}
