namespace SharpLink.UnitTests.Client;

public class AbandonedClientApplicationTerminalTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task UnassociatedApplicationTerminalKeepsDisposalPrecedence(bool canceled, bool disposalFault)
    {
        Exception original = canceled ? new OperationCanceledException("application cancellation")
            : new SharpLinkException(SharpLinkErrorCode.DeadlineExceeded, 37, "application deadline");
        var replacement = new InvalidOperationException("disposal replacement");
        var stream = new ApplicationStream(original);
        await using var fixture = new AbandonedClientOwnershipTests.Fixture(new Interceptor(stream));
        var call = AbandonedClientOwnershipTests.Invoke(fixture.Channel, RpcMethodKind.ServerStreaming, timed: false);
        try
        {
            await stream.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!call.IsCompleted, "unassociated source failure waits ordinary disposal");
            fixture.Module.TryBeginDraining();
            Ensure(!fixture.Module.WaitForDrainAsync().IsCompleted, "ordinary disposal still owns the module");
            if (disposalFault) stream.DisposeRelease.TrySetException(replacement);
            else stream.DisposeRelease.TrySetResult();
            Exception? observed = null;
            try { await call.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception exception) { observed = exception; }
            Ensure(ReferenceEquals(observed, disposalFault ? replacement : original),
                "ordinary source and disposal failures preserve identity and precedence");
            await fixture.Module.WaitForDrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { stream.DisposeRelease.TrySetResult(); }
    }

    private sealed class Interceptor(IAsyncEnumerable<int> stream) : ISharpLinkClientInterceptor
    {
        public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(
            SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
            => ValueTask.FromResult(new SharpLinkClientInvocationResult(stream));
    }

    private sealed class ApplicationStream(Exception exception) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 0;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromException<bool>(exception);
        public ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            return new ValueTask(DisposeRelease.Task);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
