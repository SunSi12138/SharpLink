using System.Diagnostics;
using System.Linq;
using System.Reflection;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRouteFailureControls
{
    [Test]
    public Task RevisionExhaustionTerminal() => WithFixture(async fixture =>
    {
        await fixture.InitializeAsync();
        await fixture.ConfirmFirstAndPrepareAsync();
        var admitted = await fixture.StartHeldFirstUnaryAsync();
        await fixture.ConfirmAsync(1);
        await fixture.JoinOriginalBindsAsync();
        var previous = fixture.View;
        var physical = previous.Targets.Single(target => target.Connection.EndpointId == "route-a").Connection;
        Ensure(physical.ActiveCallCount == 1 && !admitted.Invocation.Task.IsCompleted,
            "the real original Unary was admitted on A before exhaustion and remains physically charged");
        fixture.InjectNearLimitTopology();
        await fixture.ExpectExhaustedApplicationAsync(fixture.StartOrderedSnapshot(2, reversed: true));
        var terminal = fixture.GlobalTopology;
        Ensure(terminal.IsTerminal && terminal.Revision == ulong.MaxValue && terminal.Endpoints.Length == 0 &&
               fixture.View.Targets.Length == 0 && fixture.View.Candidates.Length == 0,
            "one terminal empty authority replaces near-limit revision without wrap or old-view restoration");
        Ensure(physical.ActiveCallCount == 1 && !admitted.Invocation.Task.IsCompleted,
            "terminal route exhaustion cannot release an admitted physical pending-call owner");
        await fixture.ExpectTerminalPrepareAndUnaryAsync();
        await fixture.ExpectExhaustedApplicationAsync(fixture.StartOrderedSnapshot(3, reversed: false));
        Ensure(ReferenceEquals(fixture.GlobalTopology, terminal) && fixture.View.Targets.Length == 0,
            "later topology attempts cannot revive an older safe view or replace the terminal authority");
        await fixture.FinishHeldFirstUnaryAsync(admitted, 83, physical);
        GC.KeepAlive(previous); // A stale externally retained view cannot become selectable again.
    });

    private sealed partial class FailureFixture
    {
        internal async Task<(OwnedOperation Invocation, TestSentFrame Request)> StartHeldFirstUnaryAsync()
        {
            var invocation = Own("original admitted Unary across exhaustion",
                ClientInvokerTestHelper.InvokeUnaryAsync(Client, cancellationToken: _calls.Token).AsTask());
            var reader = Own("original admitted Unary A reader", _transports[0].Connection.WaitForSentFrame(ProtocolV2FrameType.Request));
            var completed = await Task.WhenAny(invocation.Task, reader.Task).WaitAsync(Bound);
            if (ReferenceEquals(completed, invocation.Task)) await ObserveAsync(invocation);
            await ObserveAsync(reader);
            Ensure(!invocation.Task.IsCompleted, "the original request must be admitted but its response withheld");
            return (invocation, ((Task<TestSentFrame>)reader.Task).Result);
        }

        internal async Task JoinOriginalBindsAsync()
        {
            foreach (var operation in SnapshotOperations().Where(operation => operation.Stage == "original BindContractDemand"))
                await ObserveAsync(operation);
        }

        internal void InjectNearLimitTopology()
        {
            var cluster = Read<object>(Client, "_cluster");
            var gate = Read<Lock>(cluster, "_gate");
            lock (gate)
            {
                var old = GlobalTopology;
                Ensure(!old.IsTerminal && old.Endpoints.Length == 2, "exhaustion starts from real ready and bound topology");
                var near = new SharpLinkClient.ContractRouteTopology(ulong.MaxValue - 1, old.IndexMap,
                    old.Endpoints, old.Selection);
                typeof(SharpLinkClient).GetField("_contractRouteTopology", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .SetValue(Client, near);
            }
        }

        internal async Task ExpectExhaustedApplicationAsync(OwnedOperation launcher)
        {
            await ObserveAsync(launcher);
            var original = ((Task<OwnedOperation>)launcher.Task).Result;
            _ = await ExpectFailureAsync(original, error =>
                error is SharpLinkClient.ContractRouteFailure && IsOnlyExhaustion(error));
        }

        private static bool IsOnlyExhaustion(Exception error)
        {
            if (error is SharpLinkException sharp) return sharp.Code == SharpLinkErrorCode.ResourceExhausted;
            if (error is SharpLinkClient.ContractRouteFailure route)
                return route.Count == 1 && IsOnlyExhaustion(route.ErrorAt(0));
            if (error is AggregateException aggregate)
                return aggregate.InnerExceptions.Count == 1 && IsOnlyExhaustion(aggregate.InnerExceptions[0]);
            return false;
        }

        internal async Task ExpectTerminalPrepareAndUnaryAsync()
        {
            OwnedOperation? preparation = null;
            try { preparation = Own("original terminal Prepare", Site.PrepareAsync(Bound, _calls.Token).AsTask()); }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted) { }
            if (preparation is not null)
                await ExpectFailureAsync(preparation, error => error is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted });
            OwnedOperation? invocation = null;
            try { invocation = Own("original terminal Unary", ClientInvokerTestHelper.InvokeUnaryAsync(Client, cancellationToken: _calls.Token).AsTask()); }
            catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.ResourceExhausted) { }
            if (invocation is not null)
                await ExpectFailureAsync(invocation, error => error is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted });
        }

        internal async Task FinishHeldFirstUnaryAsync((OwnedOperation Invocation, TestSentFrame Request) admitted,
            int result, ClientConnection originalPhysical)
        {
            await ObserveAsync(Own("original admitted Unary response injection",
                _transports[0].Connection.InjectInt32ResponseAsync(unchecked((long)admitted.Request.Header.RequestId), result)));
            await ObserveAsync(admitted.Invocation);
            Ensure(((Task<int>)admitted.Invocation.Task).Result == result,
                "the original admitted invocation completes successfully on its exact pre-exhaustion session");
            var started = Stopwatch.GetTimestamp();
            while (originalPhysical.ActiveCallCount != 0 || originalPhysical.CallAdmissionReservationCount != 0)
            {
                if (Stopwatch.GetElapsedTime(started) >= Bound)
                    throw new TimeoutException("Original pre-exhaustion physical call accounting did not actually drain.");
                await Task.Delay(1);
            }
        }
    }
}
