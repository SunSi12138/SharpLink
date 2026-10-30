using System.IO.Pipelines;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ProducerReservationOwnershipTests
{
    [Test]
    [Arguments(RpcMethodKind.ClientStreaming)]
    [Arguments(RpcMethodKind.OneWay)]
    [Arguments(RpcMethodKind.DuplexStreaming)]
    public async Task LastProducerReleaseAdvancesPlannedRetirementWithoutSelfWait(RpcMethodKind kind)
    {
        await using var fixture = await Fixture.CreateAsync();
        await using var replacement = new Replacement(fixture.Client);
        var group = new ProducerGroup(1, holdDispose: true);
        var invocation = Invoke(fixture, kind, group);
        try
        {
            await group.AllDisposing.WaitAsync(Bound);
            await fixture.Cancellation.CancelAsync();
            await WaitUntilAsync(() => fixture.Connection.PendingCalls.Count == 0);
            fixture.ReleaseUnrelatedReservation();
            Ensure(fixture.Connection.CallAdmissionReservationCount == 1,
                "only the real outgoing producer now owns the source selection");
            Ensure(replacement.Connection.TryReserveSessionRefreshCommit(), "reserve the replacement cut");
            try
            {
                Ensure(replacement.Connection.TryCommitSessionRefreshRetirement(fixture.Connection),
                    "publish the existing refresh cut while the producer's Dispose remains held");
            }
            finally { replacement.Connection.ReleaseCallAdmissionReservation(); }
            fixture.Client.TryAdvancePlannedSessionRefreshRetirement(fixture.Connection);
            Ensure(fixture.Connection.HasPlannedSessionRefreshRetirement && fixture.Connection.Session.IsConnected,
                "the refresh cut cannot retire a source whose physical producer is still disposing");
            group.ReleaseAll();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
            await WaitUntilAsync(() => !fixture.Connection.Session.IsConnected);
            Ensure(!fixture.Connection.HasPlannedSessionRefreshRetirement &&
                fixture.Connection.CallAdmissionReservationCount == 0,
                "the final producer release must trigger source retirement without waiting for itself");
            await fixture.Connection.DisposeAsync();
            group.AssertCounts();
        }
        finally
        {
            group.ReleaseAll();
            await fixture.Cancellation.CancelAsync();
            _ = await CaptureFailureAsync(invocation);
            await fixture.JoinProducersAsync();
        }
    }

    private sealed class Replacement : IAsyncDisposable
    {
        private readonly Pipe _input = new();
        private readonly Pipe _output = new();
        internal ClientConnection Connection { get; }
        internal Replacement(SharpLinkClient client)
        {
            var context = (SharpLinkRuntimeContext)client.RuntimeContext;
            var session = RpcSessionTestFixture.CreateSessionOverTestTransport("producer-refresh-replacement",
                _input.Reader, _output.Writer, RpcSessionTestFixture.ClientOptions(context));
            Connection = new ClientConnection(client, session, new CancellationTokenSource(), 8, context);
        }
        public async ValueTask DisposeAsync()
        {
            await Connection.DisposeAsync();
            await _input.Writer.CompleteAsync();
            await _output.Reader.CompleteAsync();
        }
    }
}
