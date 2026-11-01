using System.IO.Pipelines;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public partial class ServerBindingTableTests
{
    [Test]
    public async Task CanceledInvalidationFlushKeepsItsControlPermitUntilActualOutputCleanup()
    {
        var input = new Pipe();
        var outputPipe = new Pipe();
        var output = new HeldInvalidationWriter(outputPipe.Writer);
        var connection = IntakeConnection(input.Reader, output);
        connection.InitializeBindings(new ServerBindingSlotQuota(1));
        await using var server = IntakeServer();
        var failureObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeBindingInvalidationPhysicalDrainForTesting = () => failureObserved.TrySetResult();
        var limit = connection.Session.BindingAuthority.Limits.QueuedControlFrames;
        var held = 0;
        for (; held < limit - 1; held++) Ensure(connection.TryReserveBindingControlFrame(limit));
        Ensure(connection.QueueBindingInvalidation(1UL << 16));
        Ensure(connection.TryTakeBindingInvalidation(out var token));
        var sending = server.TrySendBindingInvalidationAsync(connection, token).AsTask();
        Task? closing = null;
        try
        {
            await output.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!connection.TryReserveBindingControlFrame(limit));
            closing = connection.CloseAsync().AsTask(); // Cancels the real ConnectionToken/flush wait.
            await failureObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(connection.ConnectionToken.IsCancellationRequested);
            Ensure(!sending.IsCompleted && !closing.IsCompleted);
            Ensure(!connection.TryReserveBindingControlFrame(limit));
            Ensure(connection.Session.QueuedSendBytes != 0); // Actual accepted frame remains owned.
            output.Release();
            await sending.WaitAsync(TimeSpan.FromSeconds(5));
            await closing.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(connection.Session.QueuedSendBytes == 0);
            Ensure(connection.TryReserveBindingControlFrame(limit));
            Ensure(!connection.TryReserveBindingControlFrame(limit)); // Exactly one permit returned.
            connection.ReleaseBindingControlFrame();
        }
        finally
        {
            output.Release();
            while (held-- > 0) connection.ReleaseBindingControlFrame();
            try { await sending; }
            finally
            {
                await (closing ?? connection.CloseAsync().AsTask());
                await input.Writer.CompleteAsync();
                await outputPipe.Reader.CompleteAsync();
            }
        }
    }

    private sealed class HeldInvalidationWriter(PipeWriter inner) : PipeWriter
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release() => _release.TrySetResult();
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override void CancelPendingFlush() => inner.CancelPendingFlush();
        public override void Complete(Exception? exception = null) => inner.Complete(exception);
        public override ValueTask CompleteAsync(Exception? exception = null) => inner.CompleteAsync(exception);
        public override async ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await _release.Task.ConfigureAwait(false); // Physical hold deliberately ignores logical cancellation.
            return await inner.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
