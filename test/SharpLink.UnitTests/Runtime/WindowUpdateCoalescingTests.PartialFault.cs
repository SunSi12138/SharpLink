using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public partial class WindowUpdateCoalescingTests
{
    [Test]
    public async Task CompactBatchPartialStreamWriteMustNotReplayOnFaultDisposal()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var stream = new PrefixFaultStream();
        var session = new RpcSession(new StreamTransportConnection(stream), RpcSessionTestFixture.ClientOptions(context));
        RpcSessionTestFixture.CompleteHandshake(session);
        var owners = new List<CountingOwner>();
        try
        {
            session.SendPacket(new CountingOwner(Ping()));
            await stream.FirstWriteEntered.Task.WaitAsync(Timeout);
            var pump = typeof(RpcSession).GetField("_pump", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(session)!;
            var enqueue = pump.GetType().GetMethod("TryEnqueue", BindingFlags.Instance | BindingFlags.Public)!;
            for (var index = 0; index < 100; index++)
            {
                var owner = new CountingOwner(Update(1, 1, 4));
                owners.Add(owner);
                var result = enqueue.Invoke(pump,
                    [new OwnedFrame(owner, false, null, true, isCoalescibleWindowUpdate: true)]);
                Ensure(result?.ToString() == "Accepted", "each original update must enter the actual send pump");
            }
            Ensure(session.QueuedSendBytes == 23 + 2100, "the held stream write must retain all original reservations");
            stream.ReleaseFirstWrite.TrySetResult();
            await stream.SecondWriteEntered.Task.WaitAsync(Timeout);
            await WaitUntilAsync(() => !session.IsConnected && session.QueuedSendBytes == 0);
            await session.DisposeAsync();
            Ensure(stream.SecondWriteLength == 21, "the real StreamTransport writer must receive the compact frame");
            Ensure(stream.WriteCount == 2 && stream.BytesWritten == 23 + 7,
                "the failing stream must retain only its seven-byte compact-frame prefix, never a disposal replay");
            Ensure(stream.DisposeCount == 1, "the failed stream must be physically disposed once");
            foreach (var owner in owners)
                Ensure(owner.ReturnCount == 1, "fault cleanup must release every original compacted owner exactly once");
        }
        finally
        {
            stream.ReleaseFirstWrite.TrySetResult();
            await session.DisposeAsync();
        }
    }

    private sealed class CountingOwner : IRpcByteBufferWriter
    {
        private readonly PooledByteBufferWriter _inner = new();
        internal int ReturnCount;
        internal CountingOwner(byte[] bytes)
        {
            bytes.CopyTo(_inner.GetSpan(bytes.Length));
            _inner.Advance(bytes.Length);
        }
        public int WrittenCount => _inner.WrittenCount;
        public ReadOnlyMemory<byte> WrittenMemory => _inner.WrittenMemory;
        public Span<byte> WrittenSpan => _inner.WrittenSpan;
        public int Capacity => _inner.Capacity;
        public void Clear() => _inner.Clear();
        public void Advance(int count) => _inner.Advance(count);
        public Memory<byte> GetMemory(int sizeHint = 0) => _inner.GetMemory(sizeHint);
        public Span<byte> GetSpan(int sizeHint = 0) => _inner.GetSpan(sizeHint);
        public void Dispose()
        {
            Interlocked.Increment(ref ReturnCount);
            _inner.Dispose();
        }
    }

    private sealed class PrefixFaultStream : Stream
    {
        internal TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource SecondWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int WriteCount;
        internal int BytesWritten;
        internal int SecondWriteLength;
        internal int DisposeCount;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref WriteCount) == 1)
            {
                FirstWriteEntered.TrySetResult();
                await ReleaseFirstWrite.Task.ConfigureAwait(false);
                Interlocked.Add(ref BytesWritten, buffer.Length);
                return;
            }
            SecondWriteLength = buffer.Length;
            Interlocked.Add(ref BytesWritten, Math.Min(7, buffer.Length));
            SecondWriteEntered.TrySetResult();
            throw new IOException("injected stream failure after seven bytes of the compact batch");
        }
        protected override void Dispose(bool disposing)
        {
            Interlocked.Increment(ref DisposeCount);
            base.Dispose(disposing);
        }
    }
}
