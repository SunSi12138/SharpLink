using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using TUnit.Core;

namespace SharpLink.IntegrationTests;

// Test-only diagnostic: deliberately exposes whether public success can beat disposal failure.
public sealed class GatedDisposalDiagnosticTests
{
    [Test]
    public async Task ObserveSuccessWhileProducerDisposeIsStillGated()
    {
        await using var harness = await ExtensionFaultHarness.CreateAsync();
        var producer = new GatedProducer();
        var call = harness.Service.UploadAsync(producer).AsTask();
        bool completedBeforeDisposal;
        int? result = null;
        Exception? failure = null;
        try
        {
            await producer.DisposeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            completedBeforeDisposal = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(2))) == call;
            if (completedBeforeDisposal)
            {
                try { result = await call; }
                catch (Exception error) { failure = error; }
            }
        }
        finally
        {
            producer.ReleaseDispose.TrySetResult();
        }
        await producer.DisposeFinished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.AssertReusableAsync("gated disposal diagnostic");
        var observation = $"completedBeforeDisposal={completedBeforeDisposal};result={result};failure={failure?.GetType().Name ?? "none"}";
        var output = Environment.GetEnvironmentVariable("ISSUE736_DIAG_OUTPUT") ?? throw new InvalidOperationException("Missing diagnostic output path.");
        await File.AppendAllTextAsync(output, observation + Environment.NewLine);
        if (!completedBeforeDisposal || result != 7 || failure is not null)
            throw new InvalidOperationException("Expected observed clean-EOF-before-disposal behavior: " + observation);
    }

    private sealed class GatedProducer : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        private int _moves;
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeFinished { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync() => ValueTask.FromResult(Interlocked.Increment(ref _moves) == 1);
        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task.ConfigureAwait(false);
            DisposeFinished.TrySetResult();
            throw new InvalidOperationException("diagnostic gated DisposeAsync failure");
        }
    }
}
