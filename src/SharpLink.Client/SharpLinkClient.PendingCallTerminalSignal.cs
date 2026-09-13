namespace SharpLink.Client;

internal sealed partial class SharpLinkClient
{
    /// <summary>
    /// Bridges the terminal result already selected by <see cref="PendingRequestTable"/> to a
    /// framework wait that only needs a lifetime signal. It never selects or rewrites the terminal
    /// reason itself.
    /// </summary>
    private sealed class PendingCallTerminalSignal : IPendingCallCompletionObserver
    {
        private readonly IPendingCallCompletionObserver? _inner;
        private CancellationTokenSource? _source;

        public PendingCallTerminalSignal(IPendingCallCompletionObserver? inner)
        {
            _inner = inner;
            var source = new CancellationTokenSource();
            _source = source;
            Token = source.Token;
        }

        public CancellationToken Token { get; }

        public void OnResponseObserved()
            => _inner?.OnResponseObserved();

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            StopObservingTerminal(cancel: true);
            _inner?.OnPendingCallCompleted(in completion);
        }

        /// <summary>
        /// Releases the signal source after the framework wait completed normally. The wrapper
        /// remains attached to the pending entry so the original completion observer still receives
        /// the eventual terminal result.
        /// </summary>
        public void StopObservingTerminal()
            => StopObservingTerminal(cancel: false);

        private void StopObservingTerminal(bool cancel)
        {
            var source = Interlocked.Exchange(ref _source, null);
            if (source is null)
                return;

            try
            {
                if (cancel)
                    source.Cancel();
            }
            catch
            {
                // PendingRequestTable already chose the terminal result. A callback attached to this
                // framework-only signal must never interrupt or replace that authoritative terminal.
            }
            finally
            {
                source.Dispose();
            }
        }
    }
}
