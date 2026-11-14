#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    internal Func<long, CancellationToken, ValueTask>? BeforeStreamProducerClaimTestHook;

    // Seal and task publication share the existing pool/supervisor gate. User code can
    // run only after its physical owner has been registered, never in the receive loop.
    private Task TrackBoundStreamWork(Func<Task> work, string name)
    {
        lock (_poolGate)
        {
            if (_poolStopping || Volatile.Read(ref _stopStarted) != 0) throw CreateConnectionClosedException("Client is stopping.");
            var published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var task = Task.Run(async () => { await published.Task.ConfigureAwait(false); await work().ConfigureAwait(false); });
            TrackFrameworkTask(task, name);
            published.TrySetResult();
            return task;
        }
    }

    private async Task RunAcceptedClientStreamsAsync<TStreams>(ClientConnection connection, TStreams streams,
        long requestId, PendingStreamAdmission admission, SharpLinkDynamicModuleLease producerLease)
        where TStreams : struct, IRpcClientStreamWriter
    {
        try
        {
            if (!await admission.Acceptance.ConfigureAwait(false)) return;
            if (BeforeStreamProducerClaimTestHook is { } hook) await hook(requestId, admission.Token).ConfigureAwait(false);
            if (!connection.PendingCalls.TryStartStreamProducer(requestId, admission)) return;
            await streams.WriteAsync(connection, requestId, admission.Token).ConfigureAwait(false);
        }
        catch (Exception failure) { connection.PendingCalls.TryComplete(requestId, PendingCallCompletionReason.SendFailure, failure); }
        finally { producerLease.Dispose(); admission.ReleaseProducer(); }
    }
}
#endif
