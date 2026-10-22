namespace SharpLink.IntegrationTests;

[NotInParallel]
public sealed class ExtensionFaultContainmentProducerTerminalTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ClientStreamDisposeFailureMustRespectSelectedTerminalAndDrainProducer(bool replyWins)
    {
        var reply = new ClientStreamReplyGate();
        var producer = new GatedDisposeFailureProducer();
        await using var harness = await ExtensionFaultHarness.CreateAsync(new ExtensionFaultHarnessOptions
        {
            ServerInterceptors = [reply]
        });
        var call = harness.Service.UploadAsync(producer).AsTask();
        try
        {
            await producer.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await reply.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            // Dispose can suspend before the invocation continuation registers its producer task.
            await WaitForProducerStateAsync(harness, expectedActive: true).ConfigureAwait(false);
            Ensure(!call.IsCompleted, "neither gated terminal may win before its release");
            Ensure(HasProducer(harness), "supervisor must retain the physically incomplete producer");

            if (replyWins)
            {
                reply.Release.TrySetResult();
                Ensure(await call.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false) == 7,
                    "clean EOF may commit the sum before producer disposal completes");
                await harness.AssertClientIdleAsync("reply selected while producer disposal remains gated")
                    .ConfigureAwait(false);
                Ensure(!producer.DisposeCompletion.Task.IsCompleted && HasProducer(harness),
                    "successful public completion must not be mistaken for physical producer completion");
                producer.ReleaseFailure();
            }
            else
            {
                producer.ReleaseFailure();
                var failure = await CaptureFailureAsync(call).ConfigureAwait(false);
                Ensure(ReferenceEquals(failure, producer.Failure),
                    "the disposal fault selected before the reply must remain the exact local terminal");
                reply.Release.TrySetResult();
            }

            await reply.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            await AssertProducerDrainedAsync(harness).ConfigureAwait(false);
            Ensure(producer.MoveNextCalls == 2 && producer.DisposeCalls == 1,
                "both terminal orders must enumerate and dispose exactly once");
            if (replyWins)
                Ensure(call.IsCompletedSuccessfully && call.Result == 7,
                    "a late disposal fault must not replace an already-selected successful reply");
            await harness.AssertReusableAsync($"client stream disposal terminal, replyWins={replyWins}")
                .ConfigureAwait(false);
        }
        finally
        {
            producer.ReleaseFailure();
            reply.Release.TrySetResult();
            await Task.WhenAll(
                ObserveTerminalAsync(call),
                reply.Finished.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                AssertProducerDrainedAsync(harness)).ConfigureAwait(false);
        }
    }

    internal static Task AssertProducerDrainedAsync(ExtensionFaultHarness harness)
        => WaitForProducerStateAsync(harness, expectedActive: false);

    private static async Task WaitForProducerStateAsync(ExtensionFaultHarness harness, bool expectedActive)
    {
        var deadline = Stopwatch.StartNew();
        while (HasProducer(harness) != expectedActive && deadline.Elapsed < TimeSpan.FromSeconds(5))
            await Task.Yield();
        Ensure(HasProducer(harness) == expectedActive, expectedActive
            ? "the physically incomplete producer must be registered with its supervisor"
            : "the supervised client producer must physically complete before fixture exit");
    }

    private static bool HasProducer(ExtensionFaultHarness harness)
    {
        var snapshot = ((SharpLinkClient)harness.Client).FrameworkTaskSnapshotForDiagnostics;
        Ensure(snapshot.TruncatedOperations == 0, "producer ownership diagnostics must not be truncated");
        return snapshot.Operations.Any(static operation => operation.Operation == "ClientStreamingProducer");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (exception is not TimeoutException)
        {
            return exception;
        }
    }

    private static async Task ObserveTerminalAsync(Task task)
        => _ = await CaptureFailureAsync(task).ConfigureAwait(false);

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException($"assert failed: {message}");
    }

    private sealed class ClientStreamReplyGate : ISharpLinkServerInterceptor
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Finished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            if (context.Method.Kind != RpcMethodKind.ClientStreaming)
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            try
            {
                await next(context).ConfigureAwait(false);
                Entered.TrySetResult();
                await Release.Task.ConfigureAwait(false);
            }
            finally
            {
                Finished.TrySetResult();
            }
        }
    }

    private sealed class GatedDisposeFailureProducer : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _moveNextCalls;
        private int _disposeCalls;
        internal int MoveNextCalls => Volatile.Read(ref _moveNextCalls);
        internal int DisposeCalls => Volatile.Read(ref _disposeCalls);
        internal InvalidOperationException Failure { get; } = new("injected gated DisposeAsync failure");
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeCompletion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 7;

        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;

        public ValueTask<bool> MoveNextAsync() => new(Interlocked.Increment(ref _moveNextCalls) == 1);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCalls);
            DisposeEntered.TrySetResult();
            return new ValueTask(DisposeCompletion.Task);
        }

        internal void ReleaseFailure() => DisposeCompletion.TrySetException(Failure);
    }
}
