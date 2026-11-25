using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class NStudyClientRouteTests
{
    [Test]
    public async Task RetirementClosesTheSnapshotBeforeDrainingTheOriginalPublisher()
    {
        var input = new Pipe();
        var output = new Pipe();
        var otherInput = new Pipe();
        var otherOutput = new Pipe();
        await using var owner = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "prepared", input.Reader, output.Writer, RpcSessionTestFixture.ClientOptions());
        await using var other = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "different", otherInput.Reader, otherOutput.Writer, RpcSessionTestFixture.ClientOptions());
        var route = new NStudyClientRoute(owner, default);
        if (!route.IsOpenOn(owner) || route.IsOpenOn(other) || route.TryEnter(other) || !route.TryEnter(owner))
            throw new InvalidOperationException("Only the owning physical Session may acquire the publisher.");

        Task drained;
        try
        {
            drained = route.Retire();
            if (route.IsOpenOn(owner) || route.TryEnter(owner) || drained.IsCompleted)
                throw new InvalidOperationException("The retired route must deny new appenders and still join the admitted one.");
        }
        finally
        {
            route.Leave();
        }
        await drained.WaitAsync(TimeSpan.FromSeconds(5));
        if (route.IsOpenOn(owner) || route.TryEnter(owner))
            throw new InvalidOperationException("Returning the old publisher must not reopen its route.");
    }
}
