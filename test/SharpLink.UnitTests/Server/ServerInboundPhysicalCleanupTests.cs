using System.Reflection;
using System.IO.Pipelines;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

// Actual Runtime mailbox operation and actual Server service-cleanup helper, not a copied
// cleanup algorithm. The prerequisite is an explicit component barrier, NOT a generated TCP
// duplex producer: that combined generated/retirement boundary remains a separate gate.
public sealed class ServerInboundPhysicalCleanupTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CapturedChildSurvivesRouteRemovalAndJoinsBeforeServiceAndScope(bool failOutgoing)
    {
        await using var ownerServer = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration().UseTransport(new CleanupIdleListener()).Build();
        var ownerInput = new Pipe();
        var ownerOutput = new Pipe();
        await using var ownerSession = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "captured-child-cleanup-owner", ownerInput.Reader, ownerOutput.Writer, RpcSessionTestFixture.ServerOptions());
        var ownerConnection = new ServerConnectionState(ownerSession, new RpcSessionGeneratedServerBridge(ownerSession),
            new StripedLongMap<ServerCallCancellationState>(), CancellationToken.None, TimeProvider.System);
        Ensure(ownerConnection.MarkReady(null), "real cleanup owner connection ready");
        typeof(SharpLinkServer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ownerServer, 2);
        Ensure(ownerServer.TryReserveCall(ownerConnection, mayDecode: false, out var permit) ==
            ServerCallAdmissionResult.Acquired && permit is not null, "real cleanup request owner reserved");
        var physicalOwner = permit!;
        physicalOwner.RetainPhysicalTail();
        Ensure(physicalOwner.BeginPhysicalServiceCleanup(), "cleanup phase owns the exact request permit");
        var tailReleased = false;
        var manager = new StreamManager();
        var outgoing = Signal();
        var child = new HeldChild();
        var service = new HeldService();
        var scope = new Scope();
        Task? cleanup = null;
        Ensure(manager.TryReservePreAdmissionStreams(71, 1, RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true),
            "actual retained route reserved");
        try
        {
            // The production entry boundary requires the whole declared owner BEFORE any
            // service/factory/stub entry. CompleteAll may remove lookup before logical return.
            Ensure(manager.TryCaptureRequestStreamsForInvocation(71, 1, out var entryCapture),
                "full invocation entry capture succeeds while original routes are live");
            manager.Register(71, 1, child); // Typed child is created only AFTER invocation entry capture.
            manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "Session terminated before logical service return"));
            Ensure(manager.ActiveStreamCount == 0 && !child.DisposeEntered.Task.IsCompleted,
                "Session removal cannot dispose typed child while the entry capture owns it");
            var captured = manager.AbandonCapturedRequestStreamsAsync(ref entryCapture, outgoing.Task,
                prerequisiteFailureOwnedByCaller: true);
            var lease = new ServiceLease(service, scope, disposeService: true);
            cleanup = (Task)typeof(SharpLinkServer).GetMethod("CompletePhysicalServiceCleanupAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                new object[] { physicalOwner, lease, outgoing.Task, captured })!;
            Ensure(manager.ActiveStreamCount == 0 && !child.DisposeEntered.Task.IsCompleted &&
                !service.DisposeEntered.Task.IsCompleted && !cleanup.IsCompleted,
                "map removal/peer terminal cannot release the captured physical child before outgoing tail");
            if (failOutgoing) outgoing.TrySetException(new InvalidOperationException("outgoing physical failure"));
            else outgoing.TrySetResult();
            physicalOwner.ReleasePhysicalTail();
            tailReleased = true;
            await child.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!service.DisposeEntered.Task.IsCompleted && scope.Disposals == 0 && !cleanup.IsCompleted,
                "child asynchronous Dispose remains physically owned despite route removal");
            child.AllowDispose.TrySetResult();
            await service.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(scope.Disposals == 0 && !cleanup.IsCompleted, "service disposal precedes scope release");
            service.AllowDispose.TrySetResult();
            Exception? failure = null;
            try { await cleanup.WaitAsync(Timeout); }
            catch (Exception error) when (error is not TimeoutException) { failure = error; }
            Ensure((failure is not null) == failOutgoing && child.Disposals == 1 && service.Disposals == 1 &&
                scope.Disposals == 1 && manager.ActiveStreamCount == 0,
                "all independent physical/service owners finish once even after outgoing failure");
            if (failOutgoing)
            {
                var raw = failure is PhysicalTaskFailureException marked ? marked.RawException : failure;
                IEnumerable<Exception> errors = raw is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : new[] { raw! };
                Ensure(errors.Any(error => error.Message == "outgoing physical failure"), "original outgoing error retained");
            }
        }
        finally
        {
            outgoing.TrySetResult();
            if (!tailReleased) physicalOwner.ReleasePhysicalTail();
            child.AllowDispose.TrySetResult();
            service.AllowDispose.TrySetResult();
            if (cleanup is not null)
            {
                try { await cleanup.WaitAsync(Timeout); }
                catch (Exception error) when (error is not TimeoutException && failOutgoing) { }
            }
            manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed, "fixture cleanup"));
            if (cleanup is null) physicalOwner.FinishPhysicalServiceCleanup();
            physicalOwner.Dispose();
            Ensure(ownerConnection.ActiveCalls == 0 && ownerServer.ActiveCallCountForDiagnostics == 0,
                "actual cleanup owner releases both existing capacity scopes");
            typeof(SharpLinkServer).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(ownerServer, 3);
            await ownerConnection.CloseAsync();
            await ownerConnection.ServiceCleanupTask;
            await ownerInput.Writer.CompleteAsync();
            await ownerOutput.Reader.CompleteAsync();
        }
    }

    [Test]
    public void CompleteAllBeforeInvocationEntryCannotReturnDefaultSuccess()
    {
        var manager = new StreamManager();
        Ensure(manager.TryReservePreAdmissionStreams(72, 1, RpcSessionTestFixture.RuntimeContext.Buffers,
            static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true),
            "actual route reserved before close");
        manager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
            "Session terminated before service entry"));
        Ensure(!manager.TryCaptureRequestStreamsForInvocation(72, 1, out var capture),
            "missing/closed declared route is refusal, not a default successful physical owner");
        capture.Dispose();
        Ensure(manager.ActiveStreamCount == 0, "refused entry owns no route or capture to leak");
    }

    [Test]
    public async Task OriginalSetupFailureWaitsCapturedChildBeforeExistingRequestOwnerRefund()
    {
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .DisableAutomaticServiceRegistration().UseTcp(0, IPAddress.Loopback).Build();
        await server.StartAsync().AsTask().WaitAsync(Timeout);
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "captured-invocation-setup-failure", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var connection = new ServerConnectionState(session, new RpcSessionGeneratedServerBridge(session),
            new StripedLongMap<ServerCallCancellationState>(), CancellationToken.None, TimeProvider.System);
        Ensure(connection.MarkReady(null), "real connection admits the original request owner");
        ServerRequestPermit? permit = null;
        var child = new HeldChild();
        Task? failureTask = null;
        var original = new InvalidOperationException("original service acquisition failed");
        try
        {
            Ensure(server.TryReserveCall(connection, mayDecode: false, out permit) == ServerCallAdmissionResult.Acquired,
                "existing request capacity acquired, no alternative test counter");
            var owner = permit!;
            owner.Activate();
            Ensure(session.StreamManager.TryReservePreAdmissionStreams(73, 1, session.RuntimeContext.Buffers,
                static _ => true, static _ => { }, static () => { }, retainUntilLocalCompletion: true),
                "original complete inbound domain");
            Ensure(session.StreamManager.TryCaptureRequestStreamsForInvocation(73, 1, out var capture),
                "original handle acquired before a service acquisition failure");
            owner.MarkInboundStreamCleanupOwned();
            session.StreamManager.Register(73, 1, child); // Simulates factory work before it fails.
            session.StreamManager.CompleteAll(new SharpLinkException(SharpLinkErrorCode.ConnectionClosed,
                "close before setup failure returns"));
            var pending = (ValueTask)typeof(SharpLinkServer).GetMethod("CompleteFailedCapturedInvocationSetupAsync",
                BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null,
                new object[] { original, session, owner, capture })!;
            failureTask = pending.AsTask();
            await child.DisposeEntered.Task.WaitAsync(Timeout);
            Ensure(!failureTask.IsCompleted && connection.ActiveCalls == 1 && server.ActiveCallCountForDiagnostics == 1,
                "setup error cannot release original capacity while captured child Dispose is blocked");
            child.AllowDispose.TrySetResult();
            Exception? observed = null;
            try { await failureTask.WaitAsync(Timeout); }
            catch (Exception error) when (error is not TimeoutException) { observed = error; }
            Ensure(ReferenceEquals(observed, original) && child.Disposals == 1,
                "original setup failure is preserved after exact independent physical cleanup");
            owner.Dispose();
            Ensure(connection.ActiveCalls == 0 && server.ActiveCallCountForDiagnostics == 0,
                "only the existing outer request owner refunds capacity after helper completion");
        }
        finally
        {
            child.AllowDispose.TrySetResult();
            if (failureTask is not null)
            {
                try { await failureTask.WaitAsync(Timeout); }
                catch (Exception error) when (ReferenceEquals(error, original)) { }
            }
            permit?.Dispose();
            await connection.CloseAsync();
            await connection.ServiceCleanupTask;
            await output.Reader.CompleteAsync();
            await input.Writer.CompleteAsync();
        }
    }

    private sealed class HeldChild : IStreamDispatcher, IAsyncDisposable
    {
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource AllowDispose { get; } = Signal();
        internal int Disposals;
        public ValueTask DispatchAsync(ReadOnlySequence<byte> payload) => ValueTask.CompletedTask;
        public void Complete(bool isError, string? errorMessage) { }
        public void Complete(Exception? exception) { }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            await AllowDispose.Task;
        }
    }
    private sealed class HeldService : IAsyncDisposable
    {
        internal TaskCompletionSource DisposeEntered { get; } = Signal();
        internal TaskCompletionSource AllowDispose { get; } = Signal();
        internal int Disposals;
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref Disposals);
            DisposeEntered.TrySetResult();
            await AllowDispose.Task;
        }
    }
    private sealed class Scope : IServiceScope
    {
        internal int Disposals;
        public IServiceProvider ServiceProvider => throw new InvalidOperationException("No service resolution in cleanup control.");
        public void Dispose() => Interlocked.Increment(ref Disposals);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private sealed class CleanupIdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new OperationCanceledException(cancellationToken));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

}
