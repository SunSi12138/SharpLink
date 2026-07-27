using System.IO.Pipelines;
using System.IO.Pipes;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Threading.Channels;

namespace SharpLink.UnitTests.Runtime;

public class AnonymousPipeAllocatorTests
{
    [Test]
    public async Task OfferShouldRedactHandlesAndCompleteParentHandleTransfer()
    {
        await using var transport = new AnonymousPipeServerTransportListener(1);
        var offer = await transport.AllocateAsync();
        var connection = await transport.AcceptAsync();
        await using var connectionScope = connection;

        Ensure(!offer.ToString().Contains(offer.InHandle, StringComparison.Ordinal) &&
               !offer.ToString().Contains(offer.OutHandle, StringComparison.Ordinal),
            "anonymous-pipe offer diagnostics must redact inheritable handles");
        await Assert.That(offer)
            .IsEqualTo(new AnonymousPipeOffer(offer.InHandle, offer.OutHandle));
        var completeTransfer = typeof(AnonymousPipeOffer).GetMethod("CompleteHandleTransfer");
        Ensure(completeTransfer is not null,
            "anonymous-pipe offers must let the parent complete handle transfer");

        completeTransfer!.Invoke(offer, null);
        completeTransfer.Invoke(offer, null);
        var input = (AnonymousPipeServerStream)(typeof(AnonymousPipeTransportConnection)
            .GetField("_inputStream", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection) ?? throw new Exception("missing anonymous-pipe input stream"));
        var output = (AnonymousPipeServerStream)(typeof(AnonymousPipeTransportConnection)
            .GetField("_outputStream", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection) ?? throw new Exception("missing anonymous-pipe output stream"));
        Ensure(input.ClientSafePipeHandle.IsClosed && output.ClientSafePipeHandle.IsClosed,
            "completing handle transfer must close both parent-side client-handle copies");
    }

    [Test]
    public async Task FailedClientConnectionAttemptShouldStillConsumeOneShotOffer()
    {
        await using var serverOutput = new AnonymousPipeServerStream(
            PipeDirection.Out,
            HandleInheritability.Inheritable);
        await using var factory = new AnonymousPipeClientTransportFactory(
            serverOutput.GetClientHandleAsString(),
            "invalid-anonymous-pipe-handle");

        var firstFailure = await CaptureFailureAsync(factory.ConnectAsync().AsTask());
        var secondFailure = await CaptureFailureAsync(factory.ConnectAsync().AsTask());

        Ensure(firstFailure is not null,
            "the invalid second handle must fail the first connection attempt");
        Ensure(secondFailure is InvalidOperationException,
            $"a consumed one-shot offer must reject retry, not fail as {secondFailure?.GetType().Name ?? "success"}");
    }

    [Test]
    public async Task FullOfferQueueShouldFailFastAndDisposeShouldRejectFurtherOffers()
    {
        var transport = new AnonymousPipeServerTransportListener(2);
        try
        {
            _ = await transport.AllocateAsync();
            _ = await transport.AllocateAsync();

            await ExpectSharpLinkError(
                transport.AllocateAsync().AsTask(),
                SharpLinkErrorCode.ResourceExhausted);

            await transport.DisposeAsync();
            await ExpectException<ObjectDisposedException>(transport.AllocateAsync().AsTask());
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Test]
    public async Task CanceledOfferShouldNotAllocateHandles()
    {
        await using var transport = new AnonymousPipeServerTransportListener(1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await ExpectException<OperationCanceledException>(
            transport.AllocateAsync(cancellation.Token).AsTask());

        _ = await transport.AllocateAsync();
    }

    [Test]
    public async Task CanceledConsumerShouldNotPoisonOfferQueue()
    {
        await using var transport = new AnonymousPipeServerTransportListener(1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await ExpectException<OperationCanceledException>(
            transport.AcceptAsync(cancellation.Token).AsTask());

        _ = await transport.AllocateAsync();
        var connection = await transport.AcceptAsync();
        await connection.DisposeAsync();
    }

    [Test]
    public async Task ConcurrentDisposeCallersShouldAwaitQueuedConnectionCleanup()
    {
        var transport = new AnonymousPipeServerTransportListener(1);
        var connection = new BlockingDisposeConnection();
        GetOffers(transport).Writer.TryWrite(connection);

        var first = transport.DisposeAsync().AsTask();
        await connection.DisposeStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = transport.DisposeAsync().AsTask();
        var returnedBeforeCleanup = second.IsCompleted;
        connection.ReleaseDispose();
        await Task.WhenAll(first, second);

        Ensure(!returnedBeforeCleanup,
            "concurrent listener disposers must join queued connection cleanup");
    }

    [Test]
    public async Task DisposeShouldContinueAfterQueuedConnectionCleanupFailure()
    {
        var transport = new AnonymousPipeServerTransportListener(2);
        var first = new ThrowingDisposeConnection("first queued cleanup failed");
        var second = new TrackingDisposeConnection();
        GetOffers(transport).Writer.TryWrite(first);
        GetOffers(transport).Writer.TryWrite(second);

        var failure = await CaptureFailureAsync(transport.DisposeAsync().AsTask());

        Ensure(ContainsMessage(failure, "first queued cleanup failed"),
            "listener cleanup must retain the queued connection failure");
        Ensure(second.DisposeCount == 1,
            "listener cleanup must continue through later queued connections");
    }

    private static Channel<ITransportConnection> GetOffers(AnonymousPipeServerTransportListener transport)
        => (Channel<ITransportConnection>)(typeof(AnonymousPipeServerTransportListener)
            .GetField("_offers", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(transport) ?? throw new Exception("missing anonymous-pipe offer queue"));

    private static async Task<Exception> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
            throw new Exception("expected listener cleanup failure");
        }
        catch (Exception exception)
        {
            return exception;
        }
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
        return exception.InnerException is { } nested && ContainsMessage(nested, message);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async Task ExpectSharpLinkError(Task task, SharpLinkErrorCode code)
    {
        try
        {
            await task;
            throw new Exception($"expected {code}");
        }
        catch (SharpLinkException ex) when (ex.Code == code)
        {
        }
    }

    private static async Task ExpectException<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }

    private class TrackingDisposeConnection : ITransportConnection
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        private int _disposeCount;

        public string Id { get; } = Guid.NewGuid().ToString("N");
        public PipeReader Input => _input.Reader;
        public PipeWriter Output => _output.Writer;
        public EndPoint? LocalEndPoint => null;
        public EndPoint? RemoteEndPoint => null;
        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public virtual ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ThrowingDisposeConnection(string message) : TrackingDisposeConnection
    {
        public override ValueTask DisposeAsync()
        {
            _ = base.DisposeAsync();
            return ValueTask.FromException(new ApplicationException(message));
        }
    }

    private sealed class BlockingDisposeConnection : TrackingDisposeConnection
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource DisposeStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ValueTask DisposeAsync()
        {
            _ = base.DisposeAsync();
            DisposeStarted.TrySetResult();
            return new ValueTask(_release.Task);
        }

        internal void ReleaseDispose() => _release.TrySetResult();
    }
}
