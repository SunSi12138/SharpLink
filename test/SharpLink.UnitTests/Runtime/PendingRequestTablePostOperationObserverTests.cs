using System.Buffers;
using System.Reflection;
using System.Threading;
using SharpLink.Client;

namespace SharpLink.UnitTests.Runtime;

public sealed class PendingRequestTablePostOperationObserverTests
{
    [Test]
    public async Task PostOperationObserverMustSeeTerminalUnaryOperationForSuccessAndFailure()
    {
        using var table = new PendingRequestTable(
            8, PendingRequestTableTestFixture.Codecs, PendingRequestTableTestFixture.Owner, TimeProvider.System);

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

    [Test]
    public async Task DisposeRacingPublishedRegistrationMustPropagateTerminalOwnerFailure()
    {
        using var owner = new BlockingThrowingOwner();
        using var table = PendingRequestTableTestFixture.Create(8, owner);
        var registering = Task.Run(() => table.Rent(
            Int32Codec.Instance, PendingCallKind.Unary, default,
            CancellationToken.None, out _));

        Ensure(owner.RegisteredEntered.Wait(TimeSpan.FromSeconds(10)),
            "the registrar must publish the slot before blocking in owner notification");

        var disposing = Task.Run(table.Dispose);
        var disposedField = typeof(PendingRequestTable).GetField(
            "_disposed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("disposed flag not found");

        try
        {
            Ensure(SpinWait.SpinUntil(
                    () => (int)disposedField.GetValue(table)! != 0,
                    TimeSpan.FromSeconds(10)),
                "disposal must begin while the registrar is blocked");
        }
        finally
        {
            owner.AllowRegistrationToFinish();
        }

        // Either the registering thread or the disposer may win the already-published
        // terminal slot. Both paths must propagate an owner invariant failure instead
        // of silently pretending registration/cleanup succeeded.
        Exception? registrationFailure = null;
        RpcRequestOperation<int>? operation = null;
        try
        {
            operation = await registering.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception failure)
        {
            registrationFailure = failure;
        }

        Exception? disposalFailure = null;
        try
        {
            await disposing.WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (Exception failure)
        {
            disposalFailure = failure;
        }

        if (operation is not null)
        {
            Exception? operationFailure = null;
            try
            {
                _ = await operation.AsValueTask().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (Exception failure)
            {
                operationFailure = failure;
            }

            Ensure(operationFailure is SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
                "published operation must become terminal even if the owner callback fails");
        }

        Ensure(registrationFailure is InvalidOperationException { Message: "injected terminal owner failure" }
               || disposalFailure is InvalidOperationException { Message: "injected terminal owner failure" },
            "the authoritative terminal owner failure must escape whichever thread wins");
        Ensure(owner.TerminalCount == 1, "terminal owner notification must be exactly once");
        Ensure(table.Count == 0 && table.ActiveCount == 0,
            "failed owner notification must not strand the pending slot or capacity");
    }

    private sealed class BlockingThrowingOwner : IPendingCallOwner, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _terminalCount;

        public ManualResetEventSlim RegisteredEntered => _entered;
        public int TerminalCount => Volatile.Read(ref _terminalCount);

        public void OnPendingCallRegistered()
        {
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("test registration gate was not released");
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            Interlocked.Increment(ref _terminalCount);
            throw new InvalidOperationException("injected terminal owner failure");
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }

        public void AllowRegistrationToFinish() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    [Test]
    public async Task RegistrationOwnerThrowBeforeMarkRegisteredMustTerminalizeAndBalanceCapacity()
    {
        using var owner = new ThrowingRegistrationOwner(blockUntilReleased: false, throwOnTerminal: false);
        using var table = PendingRequestTableTestFixture.Create(8, owner);

        var operation = await Task.Run(() => table.Rent(
                Int32Codec.Instance, PendingCallKind.Unary, default,
                CancellationToken.None, out _, NoopPostOperationObserver.Instance))
            .WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await CaptureFailureAsync(operation.AsValueTask().AsTask());
        Ensure(failure is InvalidOperationException { Message: "injected registration owner failure" },
            "a pre-MarkRegistered fault must be returned through the authoritative operation");
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.ActiveCount == 0,
            "registration failure must leave no pending capacity or physical owner");
        Ensure(owner.TerminalCount == 1,
            "the registration failure must complete exactly once");
    }

    [Test]
    public async Task DisposeAgainstPreMarkRegisteredOwnerFailureMustNotHangTerminalWaiter()
    {
        using var owner = new ThrowingRegistrationOwner(blockUntilReleased: true, throwOnTerminal: false);
        using var table = PendingRequestTableTestFixture.Create(8, owner);
        var renting = Task.Run(() => table.Rent(
            Int32Codec.Instance, PendingCallKind.Unary, default,
            CancellationToken.None, out _, NoopPostOperationObserver.Instance));

        Ensure(owner.RegistrationEntered.Wait(TimeSpan.FromSeconds(5)),
            "registration owner must block before MarkRegistered");
        var disposing = Task.Run(table.Dispose);
        var disposedField = typeof(PendingRequestTable).GetField(
            "_disposed", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("disposed flag not found");
        try
        {
            Ensure(SpinWait.SpinUntil(
                    () => (int)disposedField.GetValue(table)! != 0,
                    TimeSpan.FromSeconds(5)),
                "disposal must start while registration is still before its barrier");
        }
        finally
        {
            owner.AllowRegistration();
        }

        var operation = await renting.WaitAsync(TimeSpan.FromSeconds(5));
        await disposing.WaitAsync(TimeSpan.FromSeconds(5));
        var failure = await CaptureFailureAsync(operation.AsValueTask().AsTask());
        Ensure(failure is InvalidOperationException { Message: "injected registration owner failure" }
               or SharpLinkException { Code: SharpLinkErrorCode.ConnectionClosed },
            "the registering failure or the concurrent disposal must win completion");
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.ActiveCount == 0,
            "a pre-MarkRegistered failure must not strand the completion thread or capacity");
        Ensure(owner.TerminalCount == 1, "only one terminal owner is allowed");
    }

    [Test]
    public async Task RegistrationCleanupMustPropagateAuthoritativeTerminalOwnerFailure()
    {
        using var owner = new ThrowingRegistrationOwner(blockUntilReleased: false, throwOnTerminal: true);
        using var table = PendingRequestTableTestFixture.Create(8, owner);

        var failure = await CaptureFailureAsync(Task.Run(() =>
        {
            _ = table.Rent(
                Int32Codec.Instance, PendingCallKind.Unary, default,
                CancellationToken.None, out _);
        }).WaitAsync(TimeSpan.FromSeconds(5)));

        Ensure(failure is InvalidOperationException { Message: "injected terminal cleanup failure" },
            "the terminal-owner failure must not be suppressed as a registration diagnostic");
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.ActiveCount == 0,
            "terminal cleanup error must not leak pending capacity");
        Ensure(owner.TerminalCount == 1, "owner terminal exception must be observed once");
    }

    [Test]
    public async Task StreamingRegistrationFailureMustCleanupAndStillPropagateRegistrationException()
    {
        using var owner = new ThrowingRegistrationOwner(blockUntilReleased: false, throwOnTerminal: false);
        using var table = PendingRequestTableTestFixture.Create(8, owner);
        var failure = await CaptureFailureAsync(Task.Run(() =>
        {
            _ = table.RegisterStream(
                PendingCallKind.ServerStreaming,
                new NoopStreamDispatcher(),
                default,
                CancellationToken.None);
        }).WaitAsync(TimeSpan.FromSeconds(5)));

        Ensure(failure is InvalidOperationException { Message: "injected registration owner failure" },
            "non-unary registration failure must not be silently converted to a successful stream lease");
        Ensure(table.Count == 0 && table.ActiveCount == 0 && owner.ActiveCount == 0,
            "failed streaming registration must not retain a pending slot or owner");
        Ensure(owner.TerminalCount == 1,
            "failed streaming registration must still take the authoritative terminal path once");
    }

    private static async Task<Exception?> CaptureFailureAsync(Task operation)
    {
        try
        {
            await operation;
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private sealed class ThrowingRegistrationOwner(bool blockUntilReleased, bool throwOnTerminal)
        : IPendingCallOwner, IDisposable
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(!blockUntilReleased);
        private int _activeCount;
        private int _terminalCount;

        public ManualResetEventSlim RegistrationEntered => _entered;
        public int ActiveCount => Volatile.Read(ref _activeCount);
        public int TerminalCount => Volatile.Read(ref _terminalCount);

        public void OnPendingCallRegistered()
        {
            Interlocked.Increment(ref _activeCount);
            _entered.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("registration gate was not released");
            throw new InvalidOperationException("injected registration owner failure");
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
            Interlocked.Decrement(ref _activeCount);
            Interlocked.Increment(ref _terminalCount);
            if (throwOnTerminal)
                throw new InvalidOperationException("injected terminal cleanup failure");
        }

        public void OnProducerCancellationCallbackFailed(Exception exception)
        {
        }

        public void AllowRegistration() => _release.Set();

        public void Dispose()
        {
            _release.Set();
            _entered.Dispose();
            _release.Dispose();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class NoopStreamDispatcher : IStreamDispatcher
    {
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;

        public void Complete(bool isError, string? errorMessage)
        {
        }

        public void Complete(Exception? exception)
        {
        }
    }

    private sealed class NoopPostOperationObserver : IPendingCallPostOperationObserver
    {
        public static readonly NoopPostOperationObserver Instance = new();

        public void OnResponseObserved()
        {
        }

        public void OnPendingCallCompleted(in PendingCallCompletion completion)
        {
        }
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
