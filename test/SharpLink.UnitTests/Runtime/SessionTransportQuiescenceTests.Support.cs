using System.IO.Pipelines;
using System.Net;

namespace SharpLink.UnitTests.Runtime;

public partial class SessionTransportQuiescenceTests
{
    private sealed class PendingWriteStream(bool abortUnblocks) : Stream
    {
        private readonly TaskCompletionSource _interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _physicalTail = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writing;
        internal TaskCompletionSource WriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource WriteUnblocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource AbortEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Action? OnDispose { get; set; }
        internal Exception? AbortFailure { get; init; }
        internal bool FailImmediately { get; init; }
        internal int DisposeCount;
        internal int OverlappingWrites;
        internal int WritesAfterAbort;
        internal ReadOnlyMemory<byte> CapturedMemory { get; private set; }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(0);
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Volatile.Read(ref _writing) != 0)
            {
                Interlocked.Increment(ref OverlappingWrites);
                throw new NotSupportedException("second write while physical flush is pending");
            }
            if (Volatile.Read(ref DisposeCount) != 0)
            {
                Interlocked.Increment(ref WritesAfterAbort);
                throw new ObjectDisposedException(nameof(PendingWriteStream));
            }
            if (FailImmediately) throw new IOException("pump-owned write failed");
            Interlocked.Exchange(ref _writing, 1);
            CapturedMemory = buffer;
            WriteEntered.TrySetResult();
            try
            {
                // Deliberately ignore cancellation. Abort and real operation completion are
                // separate, controlled stages, just as closing a socket is not joining its IO.
                await _interrupted.Task.ConfigureAwait(false);
                WriteUnblocked.TrySetResult();
                await _physicalTail.Task.ConfigureAwait(false);
                throw new IOException("physical write ended");
            }
            finally { Volatile.Write(ref _writing, 0); }
        }
        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            OnDispose?.Invoke();
            if (abortUnblocks) _interrupted.TrySetResult();
            AbortEntered.TrySetResult();
            return AbortFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(AbortFailure);
        }
        internal void ReleaseWrite()
        {
            _interrupted.TrySetResult();
            _physicalTail.TrySetResult();
        }
    }

    private sealed class CustomTransport : ITransportConnection
    {
        private readonly PendingWriteStream _stream;
        internal CustomTransport(PendingWriteStream stream, MemoryPool<byte> pool)
        {
            _stream = stream;
            Writer = new TrackingWriter(PipeWriter.Create(stream,
                new StreamPipeWriterOptions(pool, minimumBufferSize: 1024, leaveOpen: true)));
        }
        public string Id => "noncooperative-custom-transport";
        public PipeReader Input { get; } = new Pipe().Reader;
        public PipeWriter Output => Writer;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        internal TrackingWriter Writer { get; }
        internal TaskCompletionSource DisposalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int DisposeCount;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            DisposalStarted.TrySetResult();
            await _stream.DisposeAsync();
            await Writer.CompleteAsync(new IOException("custom transport aborted"));
            await Input.CompleteAsync();
        }
    }

    private sealed class TrackingWriter(PipeWriter inner) : PipeWriter
    {
        internal TaskCompletionSource CancellationRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int CompleteCount;
        internal Exception? CancellationFailure { get; set; }
        public override bool CanGetUnflushedBytes => inner.CanGetUnflushedBytes;
        public override long UnflushedBytes => inner.UnflushedBytes;
        public override void Advance(int bytes) => inner.Advance(bytes);
        public override Memory<byte> GetMemory(int sizeHint = 0) => inner.GetMemory(sizeHint);
        public override Span<byte> GetSpan(int sizeHint = 0) => inner.GetSpan(sizeHint);
        public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
            => inner.FlushAsync(cancellationToken);
        public override void CancelPendingFlush()
        {
            inner.CancelPendingFlush();
            CancellationRequested.TrySetResult();
            if (CancellationFailure is not null) throw CancellationFailure;
        }
        public override void Complete(Exception? exception = null)
        {
            Interlocked.Increment(ref CompleteCount);
            inner.Complete(exception);
        }
        public override ValueTask CompleteAsync(Exception? exception = null)
        {
            Interlocked.Increment(ref CompleteCount);
            return inner.CompleteAsync(exception);
        }
    }

    private sealed class DisposeOnlyPipeStream(Exception? failure = null)
        : System.IO.Pipes.PipeStream(System.IO.Pipes.PipeDirection.InOut, bufferSize: 1)
    {
        internal int DisposeCount;
        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref DisposeCount);
            return failure is null ? ValueTask.CompletedTask : ValueTask.FromException(failure);
        }
    }

    private sealed class TrackingMemoryPool : MemoryPool<byte>
    {
        internal int ReturnCount;
        internal int RentCount;
        public override int MaxBufferSize => int.MaxValue;
        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            Interlocked.Increment(ref RentCount);
            return new Owner(this, new byte[Math.Max(1024, minBufferSize)]);
        }
        protected override void Dispose(bool disposing) { }
        private sealed class Owner(TrackingMemoryPool pool, byte[] bytes) : IMemoryOwner<byte>
        {
            public Memory<byte> Memory => bytes;
            public void Dispose()
            {
                Array.Fill(bytes, (byte)0xDD);
                Interlocked.Increment(ref pool.ReturnCount);
            }
        }
    }
}
