namespace SharpLink.Runtime;

/// <summary>
/// Represents framework-owned retirement work after a generation has crossed its publication commit point.
/// Caller cancellation stops only the caller's observation; it never cancels the underlying cleanup.
/// </summary>
internal readonly struct SharpLinkRetirementHandle
{
    private readonly Task _completion;

    internal SharpLinkRetirementHandle(Task completion)
        => _completion = completion ?? throw new ArgumentNullException(nameof(completion));

    internal Task Completion => _completion;

    internal ValueTask WaitAsync(CancellationToken cancellationToken = default)
        => cancellationToken.CanBeCanceled
            ? new ValueTask(_completion.WaitAsync(cancellationToken))
            : new ValueTask(_completion);

    internal ValueTask<bool> WaitAsync(
        TimeSpan timeout,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
        => SharpLinkTimer.WaitAsync(_completion, timeout, timeProvider, cancellationToken);
}

/// <summary>
/// Result-bearing form of <see cref="SharpLinkRetirementHandle"/> for committed retirement operations.
/// </summary>
internal readonly struct SharpLinkRetirementHandle<T>
{
    private readonly Task<T> _completion;

    internal SharpLinkRetirementHandle(Task<T> completion)
        => _completion = completion ?? throw new ArgumentNullException(nameof(completion));

    internal Task<T> Completion => _completion;

    internal ValueTask<T> WaitAsync(CancellationToken cancellationToken = default)
        => cancellationToken.CanBeCanceled
            ? new ValueTask<T>(_completion.WaitAsync(cancellationToken))
            : new ValueTask<T>(_completion);
}
