using System.Buffers;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class PendingRequestTablePostOperationObserverTests
{
    [Test]
    public async Task PostOperationObserverMustSeeTerminalUnaryOperationForSuccessAndFailure()
    {
        using var table = new PendingRequestTable(
            8, Int32CodecProvider.Instance, NoopOwner.Instance, TimeProvider.System);

        var successObserver = new TerminalCheckingObserver();
        var success = table.Rent(
            Int32Codec.Instance, PendingCallKind.Unary, default,
            CancellationToken.None, out var successId, successObserver);
        successObserver.Attach(success);
        var payload = new ReadOnlySequence<byte>(new byte[sizeof(int)]);
        Ensure(table.Dispatch(successId, ref payload), "response must complete the pending call");
        Ensure(successObserver.CompletedAfterOperation,
            "response observer must see the completed operation");
        _ = await success.AsValueTask();

        var failureObserver = new TerminalCheckingObserver();
        var failure = table.Rent(
            Int32Codec.Instance, PendingCallKind.Unary, default,
            CancellationToken.None, out var failureId, failureObserver);
        failureObserver.Attach(failure);
        Ensure(table.TryComplete(failureId, PendingCallCompletionReason.ConnectionClosed),
            "close must complete the pending call");
        Ensure(failureObserver.CompletedAfterOperation,
            "failure observer must see the completed operation");
        try
        {
            _ = await failure.AsValueTask();
            throw new Exception("expected terminal close failure");
        }
        catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.ConnectionClosed)
        {
        }

        Ensure(table.Count == 0 && table.ActiveCount == 0,
            "post-operation observation must not retain pending slots");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class TerminalCheckingObserver : IPendingCallPostOperationObserver
    {
        private RpcRequestOperation<int>? _operation;
        public bool CompletedAfterOperation { get; private set; }

        public void Attach(RpcRequestOperation<int> operation) => _operation = operation;

        public void OnResponseObserved()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            CompletedAfterOperation = _operation?.AsValueTask().IsCompleted == true;
            Ensure(CompletedAfterOperation,
                "a post-operation observer cannot be invoked before the operation is terminal");
        }
    }
}
