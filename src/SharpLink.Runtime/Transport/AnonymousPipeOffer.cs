namespace SharpLink.Runtime;

/// <summary>Contains the inheritable handles needed to connect an anonymous-pipe client.</summary>
/// <remarks>
/// After a child process has inherited both handles, call <see cref="CompleteHandleTransfer"/>
/// (or dispose the offer) so the server can observe that child's eventual disconnect.
/// For a client in the same process, use <see cref="CreateLocalClientTransportFactory"/> instead.
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

    /// <summary>Consumes an allocated offer to create a one-shot client factory in the server's process.</summary>
    /// <remarks>
    /// The client and server share ownership of the local safe handles. Disposing this offer or calling
    /// <see cref="CompleteHandleTransfer"/> afterward does not close those handles; connection cleanup does.
    /// Do not pass this offer's handle strings to a client in the same process.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The offer was not allocated by a listener or was already consumed.</exception>
    public IClientTransportFactory CreateLocalClientTransportFactory()
        => _transfer?.CreateLocalClientTransportFactory()
           ?? throw new InvalidOperationException("Only a listener-allocated offer can create a local client factory.");

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
    private int _consumed;

    internal IClientTransportFactory CreateLocalClientTransportFactory()
    {
        if (Interlocked.CompareExchange(ref _consumed, 2, 0) != 0)
            throw new InvalidOperationException("The anonymous-pipe offer has already been consumed.");

        return new AnonymousPipeClientTransportFactory(output.ClientSafePipeHandle, input.ClientSafePipeHandle);
    }

    internal void Complete()
    {
        if (Interlocked.CompareExchange(ref _consumed, 1, 0) != 0)
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
