namespace SharpLink.Runtime;

// This is delivery evidence, not ownership of terminal-processing failures.
internal enum StreamSinkPublicationDisposition
{
    NotPublished,
    Published,
    PublishedConsumerAbandonment
}

// Internal capability only: void/custom dispatchers must never be inferred to have delivered.
// expectedLease is the original active lease returned by RetainForRegistration, not a fresh
// snapshot. Retain before owner callbacks; release independently after all publication attempts.
internal interface IStreamSinkPublicationReceipt
{
    bool TryRetainSinkPublication(long expectedLease, out long retainedLease);

    // Requires an unreleased successful retention for expectedLease. The ref value is committed
    // immediately after the terminal store, even if a subsequent notification throws.
    void PublishSinkTerminal(
        Exception? error,
        bool localAbort,
        long expectedLease,
        ref StreamSinkPublicationDisposition disposition);

    void ReleaseSinkPublication(long retainedLease);
}
