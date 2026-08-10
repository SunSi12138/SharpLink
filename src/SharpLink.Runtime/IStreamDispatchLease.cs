namespace SharpLink.Runtime;

/// <summary>
/// Coordinates StreamManager lookup lifetime with a pooled dispatcher's producer lifetime.
/// This prevents a removed dispatcher from returning to its pool between lookup and dispatch.
/// </summary>
internal interface IStreamDispatchLease
{
    void BindDispatchState(IStreamDispatchState state);

    ValueTask DispatchAcquiredAsync(ReadOnlySequence<byte> payload, int encodedByteCount);

    void OnDispatchesDrained();
}

internal interface IStreamDispatchState
{
    bool HasActiveDispatches { get; }

    bool IsDetached { get; }

    ValueTask WaitForDetachedAsync(CancellationToken cancellationToken);

    void Close();
}
