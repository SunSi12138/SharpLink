namespace SharpLink.UnitTests.Client;

/// <summary>
/// Observes entry into the test transport's next buffer request before advancing
/// virtual time. A scheduler delay is not evidence that SendPump owns the frame.
/// Releasing on Dispose also prevents an assertion failure from stranding the pump.
/// </summary>
internal sealed class EmissionEntryBarrier : IDisposable
{
    private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal EmissionEntryBarrier(TestTransportConnection transport)
    {
        transport.RunOnNextOutputBufferRequest(() =>
        {
            _entered.TrySetResult();
            _release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        });
    }

    internal Task WaitUntilEnteredAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
    internal void Set() => _release.TrySetResult();
    public void Dispose() => Set();
}
