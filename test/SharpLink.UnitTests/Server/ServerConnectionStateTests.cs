using System.IO.Pipelines;
using System.Threading;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public class ServerConnectionStateTests
{
    [Test]
    public async Task LifecycleShouldPublishAuthenticationAndCloseOnce()
    {
        var disconnectCount = 0;
        var state = CreateState(() => Interlocked.Increment(ref disconnectCount));
        var authentication = new SharpLinkAuthenticationContext(subject: "alice");

        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Handshaking, "initial state");
        Ensure(state.DefaultCallContext is null, "handshaking connection must not publish a call context");
        Ensure(state.MarkReady(authentication), "handshake should mark the connection ready");
        Ensure(ReferenceEquals(authentication, state.AuthenticationContext), "authentication must belong to the connection");
        var callContext = state.DefaultCallContext ??
            throw new Exception("ready connection must publish a default call context");
        Ensure(callContext.SessionId == state.Session.Id, "call context session ID");
        Ensure(ReferenceEquals(authentication, callContext.Authentication), "call context authentication");
        Ensure(callContext.Deadline is null, "default call context deadline");
        Ensure(callContext.Metadata is null, "default call context metadata");
        Ensure(ReferenceEquals(callContext, state.GetCallContextSnapshot(null, null)),
            "plain calls must reuse the default call context");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        var deadlineContext = state.GetCallContextSnapshot(deadline, null);
        Ensure(!ReferenceEquals(callContext, deadlineContext), "deadline calls must not reuse the default context");
        Ensure(deadlineContext.Deadline == deadline, "deadline call context");

        var metadata = new SharpLinkMetadata();
        var metadataContext = state.GetCallContextSnapshot(null, metadata);
        Ensure(!ReferenceEquals(callContext, metadataContext), "metadata calls must not reuse the default context");
        Ensure(ReferenceEquals(metadata, metadataContext.Metadata), "metadata call context");
        Ensure(state.TryRecordAcceptedRequest(42), "ready connection should accept request IDs");
        Ensure(state.LastAcceptedRequestId == 42, "last accepted request ID");

        state.MarkDraining();
        Ensure(!state.TryRecordAcceptedRequest(43), "draining connection must reject new request IDs");

        await Task.WhenAll(state.CloseAsync().AsTask(), state.CloseAsync().AsTask());
        Ensure(state.LifecycleState == ServerConnectionLifecycleState.Closed, "closed state");
        Ensure(state.SessionTask.IsCompletedSuccessfully, "session completion should be published");
        Ensure(state.AuthenticationContext is null, "closed connection must release authentication context");
        Ensure(state.DefaultCallContext is null, "closed connection must release default call context");
        Ensure(disconnectCount == 1, "session should disconnect exactly once");
    }

    [Test]
    public async Task DefaultCallContextShouldBeIsolatedPerConnectionAndSafeForConcurrentReads()
    {
        var first = CreateState(static () => { });
        var second = CreateState(static () => { });
        var firstAuthentication = new SharpLinkAuthenticationContext(subject: "alice");
        var secondAuthentication = new SharpLinkAuthenticationContext(subject: "bob");

        Ensure(first.MarkReady(firstAuthentication), "first ready");
        Ensure(second.MarkReady(secondAuthentication), "second ready");

        var firstContext = first.DefaultCallContext ?? throw new Exception("first context");
        var secondContext = second.DefaultCallContext ?? throw new Exception("second context");
        Ensure(!ReferenceEquals(firstContext, secondContext), "connections must not share call contexts");
        Ensure(ReferenceEquals(firstAuthentication, firstContext.Authentication), "first authentication");
        Ensure(ReferenceEquals(secondAuthentication, secondContext.Authentication), "second authentication");

        Parallel.For(0, 1024, _ =>
            Ensure(ReferenceEquals(firstContext, first.DefaultCallContext), "concurrent context read"));

        await first.CloseAsync();
        await second.CloseAsync();
    }

    [Test]
    public async Task ServerCancellationShouldCancelOnlyLinkedConnectionState()
    {
        using var serverCancellation = new CancellationTokenSource();
        var state = CreateState(static () => { }, serverCancellation.Token);
        var unrelated = CreateState(static () => { });

        serverCancellation.Cancel();

        Ensure(state.ConnectionToken.IsCancellationRequested, "linked connection token should be canceled");
        Ensure(!unrelated.ConnectionToken.IsCancellationRequested, "unrelated connection token should remain active");
        await state.CloseAsync();
        await unrelated.CloseAsync();
    }

    [Test]
    public async Task CallAdmissionShouldBePerConnectionAndRecoverCapacity()
    {
        var first = CreateState(static () => { });
        var second = CreateState(static () => { });
        Ensure(first.MarkReady(null), "first ready");
        Ensure(second.MarkReady(null), "second ready");

        Ensure(first.TryAcquireCall(1), "first call should acquire capacity");
        Ensure(!first.TryAcquireCall(1), "same connection should enforce its limit");
        Ensure(second.TryAcquireCall(1), "another connection should have independent capacity");
        first.ReleaseCall();
        Ensure(first.TryAcquireCall(1), "released capacity should be reusable");

        first.ReleaseCall();
        second.ReleaseCall();
        await first.CloseAsync();
        await second.CloseAsync();
    }

    private static ServerConnectionState CreateState(
        Action disconnect,
        CancellationToken serverToken = default)
    {
        var input = new Pipe();
        var output = new Pipe();
        var session = new RpcSession(
            Guid.NewGuid().ToString("N"),
            input.Reader,
            output.Writer,
            disconnect,
            static () => true);
        return new ServerConnectionState(session, new RuntimeConcurrencyOptions(), serverToken);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
