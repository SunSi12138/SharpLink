namespace SharpLink.Runtime;

/// <summary>Contains the inheritable handles needed to connect an anonymous-pipe client.</summary>
/// <remarks>
/// After a child process has inherited both handles, call <see cref="CompleteHandleTransfer"/>
/// (or dispose the offer) so the server can observe that child's eventual disconnect.
/// </remarks>
public readonly record struct AnonymousPipeOffer(string InHandle, string OutHandle) : IDisposable
{
    private readonly AnonymousPipeHandleTransfer? _transfer;

    internal AnonymousPipeOffer(
        string inHandle,
        string outHandle,
        AnonymousPipeHandleTransfer transfer)
        : this(inHandle, outHandle)
    {
        _transfer = transfer;
    }

    /// <summary>Closes the parent's local copies after a child process has inherited both handles.</summary>
    public void CompleteHandleTransfer() => _transfer?.Complete();

    /// <inheritdoc />
    public void Dispose() => CompleteHandleTransfer();

    /// <inheritdoc />
    public bool Equals(AnonymousPipeOffer other)
        => StringComparer.Ordinal.Equals(InHandle, other.InHandle) &&
           StringComparer.Ordinal.Equals(OutHandle, other.OutHandle);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(InHandle, OutHandle);

    /// <inheritdoc />
    public override string ToString() => "AnonymousPipeOffer { Handles = [redacted] }";
}

internal sealed class AnonymousPipeHandleTransfer(
    AnonymousPipeServerStream input,
    AnonymousPipeServerStream output)
{
    private int _completed;

    internal void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
            return;

        Exception? failure = null;
        try
        {
            input.DisposeLocalCopyOfClientHandle();
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        try
        {
            output.DisposeLocalCopyOfClientHandle();
        }
        catch (Exception exception)
        {
            failure = failure is null
                ? exception
                : new AggregateException(failure, exception);
        }

        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
