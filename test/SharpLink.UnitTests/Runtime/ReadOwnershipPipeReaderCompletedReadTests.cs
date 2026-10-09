using System.Buffers;
using System.IO.Pipelines;
using System.Threading.Tasks.Sources;

namespace SharpLink.UnitTests.Runtime;

/// <summary>Already-completed failures must not turn into queued, ownership-retaining reads.</summary>
public class ReadOwnershipPipeReaderCompletedReadTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task CompletedFailureShouldPublishAndReleaseWithoutRegisteringAContinuation(int kind)
    {
        using var cancellation = new CancellationTokenSource();
        Exception failure = kind switch
        {
            0 => new IOException("already faulted"),
            1 => new OperationCanceledException("already canceled", new CancellationToken(true)),
            2 => new OperationCanceledException("default cancellation token"),
            _ => new OperationCanceledException("token not yet canceled", cancellation.Token),
        };
        var fake = new CompletedFailurePipeReader(failure);
        var reader = new ReadOwnershipPipeReader(fake);
        ValueTask<ReadResult> read = default;
        var escaped = Capture(() => read = reader.ReadAsync());
        Ensure(escaped is null, "a completed inner failure must be returned as a ValueTask, not thrown from ReadAsync");
        Ensure(read.IsCompleted, "the outer read must already be complete when ReadAsync returns");
        Ensure(kind == 0 ? read.IsFaulted : read.IsCanceled, "the original fault/cancellation status must be preserved");
        Ensure(fake.ResultObservationCount == 1, "the completed inner read must be observed synchronously exactly once");
        Ensure(fake.ContinuationRegistrationCount == 0, "a completed read must not introduce a queued continuation hop");

        // Transport ownership is already released even though the outer failure is unobserved.
        await reader.CompleteAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        Ensure(fake.CompletionCount == 1, "transport cleanup must not depend on observing the completed failure");
        var observed = Capture(() => read.GetAwaiter().GetResult());
        Ensure(ReferenceEquals(observed, failure), "completed failure must preserve exact exception identity");
        if (failure is OperationCanceledException expected)
            Ensure(((OperationCanceledException)observed!).CancellationToken == expected.CancellationToken,
                "completed cancellation must preserve its token, including default and non-requested tokens");
    }

    [Test]
    public async Task RealStreamReaderPreCanceledTokenShouldRemainSynchronouslyCanceled()
    {
        using var stream = new MemoryStream(new byte[] { 0x71 });
        var inner = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        var reader = new ReadOwnershipPipeReader(inner);
        var token = new CancellationToken(canceled: true);
        ValueTask<ReadResult> read = default;
        Ensure(Capture(() => read = reader.ReadAsync(token)) is null,
            "the StreamPipeReader's completed cancellation must not escape ReadAsync synchronously");
        Ensure(read.IsCompleted && read.IsCanceled,
            "a StreamPipeReader pre-canceled read must stay canceled at the wrapper return boundary");
        var observed = Capture(() => read.GetAwaiter().GetResult());
        Ensure(observed is OperationCanceledException canceled && canceled.CancellationToken == token,
            "the real stream cancellation token must survive forwarding");

        var next = await reader.ReadAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
        Ensure(next.Buffer.ToArray().SequenceEqual(new byte[] { 0x71 }),
            "a fresh read after pre-cancellation must still receive the stream data");
        reader.AdvanceTo(next.Buffer.End);
        await reader.CompleteAsync().AsTask().WaitAsync(Timeout).ConfigureAwait(false);
    }

    private static Exception? Capture(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception error)
        {
            return error;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class CompletedFailurePipeReader : PipeReader, IValueTaskSource<ReadResult>
    {
        private ManualResetValueTaskSourceCore<ReadResult> _source;
        internal CompletedFailurePipeReader(Exception error) => _source.SetException(error);
        internal int ResultObservationCount { get; private set; }
        internal int ContinuationRegistrationCount { get; private set; }
        internal int CompletionCount { get; private set; }
        public override ValueTask<ReadResult> ReadAsync(CancellationToken cancellationToken = default)
            => new(this, _source.Version);
        public ReadResult GetResult(short token)
        {
            ResultObservationCount++;
            return _source.GetResult(token);
        }
        public ValueTaskSourceStatus GetStatus(short token) => _source.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
        {
            ContinuationRegistrationCount++;
            _source.OnCompleted(continuation, state, token, flags);
        }
        public override void AdvanceTo(SequencePosition consumed) { }
        public override void AdvanceTo(SequencePosition consumed, SequencePosition examined) { }
        public override void CancelPendingRead() { }
        public override void Complete(Exception? exception = null) => CompletionCount++;
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            CompletionCount++;
            return default;
        }
        public override bool TryRead(out ReadResult result) { result = default; return false; }
    }
}
