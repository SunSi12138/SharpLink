namespace SharpLink.Runtime;

/// <summary>
/// Strong local receive-stream termination used after the owning logical RPC has selected a
/// terminal result. Unlike peer StreamComplete, this boundary must stop buffered user-visible
/// delivery immediately while preserving receive-credit accounting for discarded items.
/// </summary>
internal interface IStreamLocalAbortDispatcher : IStreamDispatcher
{
    void CompleteLocalAbort(Exception? exception);

    void RetireLocalAbortBuffer();
}
