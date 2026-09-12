using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class ClientConnectionSessionRefreshCutTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task PublishedFatalFailureShouldRejectContendingCutAndKeepSourceOpen(bool repeatedRefresh)
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await using var predecessor = new ConnectionFixture(client, "fatal-first-predecessor");
        await using var source = new ConnectionFixture(client, "fatal-first-source");
        await using var replacement = new ConnectionFixture(client, "fatal-first-replacement");
        if (repeatedRefresh)
            Commit(predecessor.Connection, source.Connection);
        var priorRedirect = source.Connection.SessionRefreshRedirect;
        Ensure(replacement.Connection.TryReserveSessionRefreshCommit(), "replacement reservation acquired");

        var fatalPublished = NewSignal();
        var cutEntered = NewSignal();
        using var releaseFatal = new ManualResetEventSlim(false);
        client._afterFatalFailurePublicationTestHook = connection =>
        {
            if (!ReferenceEquals(connection, replacement.Connection))
                return;
            fatalPublished.TrySetResult();
            Wait(releaseFatal);
        };
        client._beforeSessionRefreshCutLockTestHook = connection =>
        {
            if (ReferenceEquals(connection, replacement.Connection))
                cutEntered.TrySetResult();
        };
        var failure = Task.Run(replacement.Connection.ObserveFatalFailureForAdmission);
        Task<bool>? cut = null;
        try
        {
            await fatalPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(replacement.Connection.HasObservedFatalFailureForAdmission &&
                   replacement.Connection.Session.CanAcceptCalls,
                "freeze after admission-fatal publication while the physical session remains Ready");
            Ensure(!replacement.Connection.CanAcceptCalls &&
                   !replacement.Connection.TryReserveCallAdmission(out _),
                "ordinary admission must already reject the fatal replacement");

            cut = Task.Run(() => replacement.Connection.TryCommitSessionRefreshRetirement(source.Connection));
            await cutEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!cut.IsCompleted && !failure.IsCompleted,
                "a contending cut waits while fatal publication holds the shared commit gate");
            Ensure(source.Connection.CanAcceptCalls && !source.Connection.HasPlannedSessionRefreshRetirement,
                "the healthy source stays selectable while the replacement decision is pending");
            AssertSelection(source.Connection, source.Connection);

            releaseFatal.Set();
            await failure.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!await cut.WaitAsync(TimeSpan.FromSeconds(5)),
                "fatal publication must win; the contending cut cannot commit");
            Ensure(source.Connection.CanAcceptCalls && !source.Connection.HasPlannedSessionRefreshRetirement &&
                   ReferenceEquals(source.Connection.SessionRefreshRedirect, priorRedirect) &&
                   replacement.Connection.SessionRefreshRedirect is null,
                "a rejected cut leaves source admission, retirement and the lineage redirect unchanged");
            AssertSelection(source.Connection, source.Connection);
            if (repeatedRefresh)
                AssertSelection(predecessor.Connection, source.Connection);
        }
        finally
        {
            releaseFatal.Set();
            await failure.WaitAsync(TimeSpan.FromSeconds(5));
            if (cut is not null)
                await cut.WaitAsync(TimeSpan.FromSeconds(5));
            client._afterFatalFailurePublicationTestHook = null;
            client._beforeSessionRefreshCutLockTestHook = null;
            replacement.Connection.ReleaseCallAdmissionReservation();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RedirectPublicationShouldCutOrdinaryAdmissionBeforeRetirementBookkeeping(bool repeatedRefresh)
    {
        await using var client = ClientBuilderTestHelper.Build(new TestClientTransportFactory());
        await using var predecessor = new ConnectionFixture(client, "cut-first-predecessor");
        await using var source = new ConnectionFixture(client, "cut-first-source");
        await using var replacement = new ConnectionFixture(client, "cut-first-replacement");
        if (repeatedRefresh)
            Commit(predecessor.Connection, source.Connection);
        Ensure(replacement.Connection.TryReserveSessionRefreshCommit(), "replacement reservation acquired");

        var beforePublication = NewSignal();
        var afterPublication = NewSignal();
        var failureEntered = NewSignal();
        using var releasePublication = new ManualResetEventSlim(false);
        using var releaseBookkeeping = new ManualResetEventSlim(false);
        client._beforeSessionRefreshCutPublicationTestHook = (_, _) =>
        {
            beforePublication.TrySetResult();
            Wait(releasePublication);
        };
        client._afterSessionRefreshCutPublicationTestHook = (_, _) =>
        {
            afterPublication.TrySetResult();
            Wait(releaseBookkeeping);
        };
        client._beforeFatalFailurePublicationTestHook = connection =>
        {
            if (ReferenceEquals(connection, replacement.Connection))
                failureEntered.TrySetResult();
        };

        var cut = Task.Run(() => replacement.Connection.TryCommitSessionRefreshRetirement(source.Connection));
        Task? failure = null;
        var sourceReserved = false;
        var sourceWorkStarted = false;
        try
        {
            await beforePublication.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(source.Connection.CanAcceptCalls && !replacement.Connection.CanAcceptCalls,
                "before the cut, the shared lineage still admits its source rather than the prepared replacement");
            Ensure(source.Connection.TryReserveCallAdmission(out var admitted) && ReferenceEquals(admitted, source.Connection),
                "ordinary RPC can still reserve source admission before redirect publication");
            sourceReserved = true;
            if (repeatedRefresh)
                AssertSelection(predecessor.Connection, source.Connection);

            failure = Task.Run(replacement.Connection.ObserveFatalFailureForAdmission);
            await failureEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!failure.IsCompleted && !replacement.Connection.HasObservedFatalFailureForAdmission,
                "a contending fatal transition cannot publish between the protected eligibility check and the cut");

            releasePublication.Set();
            await afterPublication.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(!cut.IsCompleted && !source.Connection.HasPlannedSessionRefreshRetirement,
                "freeze after the cut but before retirement bookkeeping and commit return");
            Ensure(!source.Connection.CanAcceptCalls && replacement.Connection.CanAcceptCalls,
                "one redirect publication closes source eligibility and opens replacement eligibility");
            AssertSelection(source.Connection, replacement.Connection);
            if (repeatedRefresh)
                AssertSelection(predecessor.Connection, replacement.Connection);
            Ensure(source.Connection.CallAdmissionReservationCount == 1,
                "post-cut selections never add source reservations and preserve the pre-cut reservation");
            Ensure(!failure.IsCompleted && !replacement.Connection.HasObservedFatalFailureForAdmission,
                "fatal publication remains excluded through the ordinary-RPC-visible cut");

            sourceWorkStarted = source.Connection.TryBeginUntrackedCall();
            sourceReserved = false;
            Ensure(sourceWorkStarted && source.Connection.ActiveCallCount == 1,
                "work admitted before the cut may register and continue on the source after the cut");

            releaseBookkeeping.Set();
            Ensure(await cut.WaitAsync(TimeSpan.FromSeconds(5)), "the cut that publishes first commits successfully");
            await failure.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(replacement.Connection.HasObservedFatalFailureForAdmission &&
                   source.Connection.HasPlannedSessionRefreshRetirement &&
                   !source.Connection.CanAcceptCalls && !replacement.Connection.CanAcceptCalls,
                "failure published after the cut follows post-cut handling without reopening source admission");
            Ensure(!source.Connection.TryReserveCallAdmission(out _),
                "stale selection must not admit work onto a replacement that failed after the cut");
            Ensure(source.Connection.ActiveCallCount == 1,
                "post-cut replacement failure does not consume previously admitted source work");
        }
        finally
        {
            releasePublication.Set();
            releaseBookkeeping.Set();
            await cut.WaitAsync(TimeSpan.FromSeconds(5));
            if (failure is not null)
                await failure.WaitAsync(TimeSpan.FromSeconds(5));
            client._beforeSessionRefreshCutPublicationTestHook = null;
            client._afterSessionRefreshCutPublicationTestHook = null;
            client._beforeFatalFailurePublicationTestHook = null;
            if (sourceReserved)
                source.Connection.ReleaseCallAdmissionReservation();
            if (sourceWorkStarted)
                source.Connection.EndUntrackedCall();
            replacement.Connection.ReleaseCallAdmissionReservation();
        }
    }

    private static void Commit(ClientConnection source, ClientConnection replacement)
    {
        Ensure(replacement.TryReserveSessionRefreshCommit(), "prior generation reservation acquired");
        try
        {
            Ensure(replacement.TryCommitSessionRefreshRetirement(source), "prior generation cut completed");
        }
        finally
        {
            replacement.ReleaseCallAdmissionReservation();
        }
    }

    private static void AssertSelection(ClientConnection snapshotConnection, ClientConnection expected)
    {
        var selected = EndpointSelectionKernel.SelectConnection([snapshotConnection]);
        try
        {
            Ensure(ReferenceEquals(selected, expected),
                "ordinary endpoint selection through the retained snapshot must reserve the expected connection");
        }
        finally
        {
            selected?.ReleaseCallAdmissionReservation();
        }
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Wait(ManualResetEventSlim signal)
    {
        if (!signal.Wait(TimeSpan.FromSeconds(5)))
            throw new TimeoutException("session-refresh race hook was not released");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class ConnectionFixture : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();

        internal ConnectionFixture(SharpLinkClient client, string id)
        {
            var context = (SharpLinkRuntimeContext)client.RuntimeContext;
            var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
                id, _input.Reader, _output.Writer, RpcSessionTestFixture.ClientOptions(context));
            Connection = new ClientConnection(client, session, new CancellationTokenSource(), 8, context);
        }

        internal ClientConnection Connection { get; }

        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }
}
