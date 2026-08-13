using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Security.Cryptography;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class TransportCleanupTests
{
    [Test]
    public async Task StreamConnectionShouldReadIntoProfiled16KiBBlocks()
    {
        var stream = new ReadSizeRecordingStream();
        await using var connection = new StreamTransportConnection(stream);

        var result = await connection.Input.ReadAsync();
        connection.Input.AdvanceTo(result.Buffer.End);

        Ensure(StreamTransportConnection.ReadBufferBytes == 16 * 1024,
            "the profiled stream read block must remain fixed at the accepted 16 KiB A/B candidate");
        Ensure(stream.LargestReadBufferBytes == 16 * 1024,
            "stream transports must request 16 KiB reads so common 4 KiB RPC frames do not systematically span every segment");
        Ensure(result.IsCompleted, "the recording stream must complete the deterministic read");
    }

    [Test]
    public async Task StreamConnectionDisposeShouldWaitForOutstandingReadRelease()
    {
        var stream = new MemoryStream(new byte[] { 1, 2, 3, 4 });
        var connection = new StreamTransportConnection(stream);
        var reader = (ReadOwnershipPipeReader)connection.Input;
        var result = await reader.ReadAsync();

        var dispose = connection.DisposeAsync().AsTask();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!reader.CompletionRequested)
            await Task.Delay(1, timeout.Token);

        Ensure(!dispose.IsCompleted,
            "stream disposal must not complete its PipeReader while a consumer owns a ReadResult");

        reader.AdvanceTo(result.Buffer.End);
        await dispose.WaitAsync(TimeSpan.FromSeconds(2));
        Ensure(!stream.CanRead,
            "the owned stream should be disposed after the consumer releases its ReadResult");
    }

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
    public async Task SessionShouldObserveTransportOwnedPipelineCompletionFailure()
    {
        var transport = new PipelineFailingTransport();
        transport.Output.Write(new byte[1]);
        var session = new RpcSession(
            transport,
            RpcSessionTestFixture.ClientOptions());

        var failure = await CaptureAsync(session.DisposeAsync);

        Ensure(failure.Message == "pipeline completion failed", "pipeline failure should remain observable");
        Ensure(transport.DisposeCount == 1, "session must dispose its transport after pipeline failure");
    }

    [Test]
    public async Task AnonymousPipeConnectionShouldDisposeInputAfterOutputCleanupFailure()
    {
        var input = new TrackingPipeStream();
        var output = new TrackingPipeStream("anonymous output cleanup failed");
        var connection = new AnonymousPipeTransportConnection(input, output);

        var failure = await CaptureAsync(connection.DisposeAsync);

        Ensure(ContainsMessage(failure, "anonymous output cleanup failed"),
            "anonymous-pipe cleanup must retain the output failure");
        Ensure(output.DisposeCount == 1, "anonymous-pipe output must be disposed once");
        Ensure(input.DisposeCount == 1,
            "anonymous-pipe input must still be disposed after output cleanup fails");
    }

    [Test]
    // The assertion compares the process-wide active shared-memory mapping counter.
    [NotInParallel]
    public async Task SharedMemoryConnectionShouldReleaseMappingAfterControlCleanupFailure()
    {
        var initialMappings = SharedMemoryMapping.ActiveMappingCount;
        var nonce = RandomNumberGenerator.GetBytes(SharedMemoryLayout.NonceBytes);
        var mapping = SharedMemoryMapping.CreateServer(64 * 1024, nonce, out _);
        var control = new SharedMemoryControlChannel(
            new TrackingPipeStream("shared-memory control cleanup failed"));
        var connection = SharedMemoryTransportConnection.Create(mapping, control, isClient: true, spinCount: 0);

        try
        {
            var failure = await CaptureAsync(connection.DisposeAsync);

            Ensure(ContainsMessage(failure, "shared-memory control cleanup failed"),
                "shared-memory cleanup must retain the control-channel failure");
            Ensure(SharedMemoryMapping.ActiveMappingCount == initialMappings,
                "shared-memory mapping must be released after control-channel cleanup fails");
        }
        finally
        {
            await mapping.DisposeAsync();
        }
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

    private static bool ContainsMessage(Exception exception, string message)
    {
        if (exception.Message == message)
            return true;
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                if (ContainsMessage(inner, message))
                    return true;
            return false;
        }
        return exception.InnerException is { } innerException && ContainsMessage(innerException, message);
    }

    private sealed class TrackingPipeStream(string? failure = null)
        : PipeStream(PipeDirection.InOut, bufferSize: 1)
    {
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return failure is null
                ? ValueTask.CompletedTask
                : ValueTask.FromException(new ApplicationException(failure));
        }
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

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Exception? failure = null;
            try
            {
                await Output.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = exception;
            }

            try
            {
                await Input.CompleteAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            try
            {
                await _outputStream.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                failure = failure is null ? exception : new AggregateException(failure, exception);
            }

            if (failure is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
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

    private sealed class ReadSizeRecordingStream : Stream
    {
        internal int LargestReadBufferBytes { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            LargestReadBufferBytes = Math.Max(LargestReadBufferBytes, count);
            return 0;
        }
        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            LargestReadBufferBytes = Math.Max(LargestReadBufferBytes, buffer.Length);
            return ValueTask.FromResult(0);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) { }
    }
}
