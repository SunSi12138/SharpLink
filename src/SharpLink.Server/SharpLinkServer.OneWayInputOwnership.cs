namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    private static async ValueTask CompleteDynamicOneWayInputAsync(
        ValueTask invocation,
        ServiceLease lease,
        StreamManager.InputStreamDrain inputDrain)
    {
        Exception? failure = null;
        try { await invocation.ConfigureAwait(false); }
        catch (Exception exception) { failure = exception; }
        try { await inputDrain.CompleteAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = CombineTerminalExceptions(failure, exception); }
        try { await lease.DisposeAsync().ConfigureAwait(false); }
        catch (Exception exception) { failure = CombineTerminalExceptions(failure, exception); }
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}

