using System.IO.Pipelines;
using System.Net;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class TransportCleanupTests
{
    [Test]
    public async Task StreamConnectionShouldDisposeOwnedStreamAfterWriterCompletionFailure()
    {
        var stream = new ThrowingWriteStream();
        var connection = new StreamTransportConnection(stream);
        connection.Output.Write(new byte[1]);

        var failure = await CaptureAsync(connection.DisposeAsync);

        Ensure(failure.Message == "writer completion failed", "writer failure should remain observable");
        Ensure(stream.DisposeCount == 1, "owned stream must be disposed after writer completion failure");
    }

    [Test]
    public async Task SessionShouldDisposeTransportAfterPipelineCompletionFailure()
    {
        var transport = new PipelineFailingTransport();
        transport.Output.Write(new byte[1]);
        var session = new RpcSession(transport);

        var failure = await CaptureAsync(session.DisposeAsync);

        Ensure(failure.Message == "pipeline completion failed", "pipeline failure should remain observable");
        Ensure(transport.DisposeCount == 1, "session must dispose its transport after pipeline failure");
    }

    private static async Task<Exception> CaptureAsync(Func<ValueTask> action)
    {
        try
        {
            await action();
            throw new Exception("expected cleanup failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class PipelineFailingTransport : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly ThrowingWriteStream _outputStream = new("pipeline completion failed");
        private int _disposeCount;

        public string Id { get; } = "pipeline-failing";
        public PipeReader Input => _input.Reader;
        public PipeWriter Output { get; }
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal PipelineFailingTransport()
            => Output = PipeWriter.Create(_outputStream, new StreamPipeWriterOptions(leaveOpen: true));

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingWriteStream(string message = "writer completion failed") : Stream
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count)
            => throw new ApplicationException(message);

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException(new ApplicationException(message));

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
