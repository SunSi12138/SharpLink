using System.IO.Pipelines;

namespace SharpLink.UnitTests.Runtime;

public sealed class NStudyClientRouteTests
{
    [Test]
    [Arguments(1)]
    [Arguments(3)]
    public async Task RetirementClosesTheSnapshotBeforeDrainingOriginalPublishers(int publisherCount)
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
        if (!route.IsOpenOn(owner) || route.IsOpenOn(other) || route.TryEnter(other))
            throw new InvalidOperationException("Only the owning physical Session may acquire the publisher.");

        var remaining = 0;
        Task drained, repeated;
        try
        {
            for (; remaining < publisherCount; remaining++)
                if (!route.TryEnter(owner))
                    throw new InvalidOperationException("The owning Session may acquire concurrent appenders.");
            drained = route.Retire();
            repeated = route.Retire();
            if (route.IsOpenOn(owner) || route.TryEnter(owner) || drained.IsCompleted || repeated.IsCompleted)
                throw new InvalidOperationException("Retirement must deny new appenders and still join admitted ones.");
            while (remaining > 1)
            {
                route.Leave();
                remaining--;
                if (drained.IsCompleted || repeated.IsCompleted)
                    throw new InvalidOperationException("The drain must wait for the last admitted appender.");
            }
        }
        finally
        {
            for (; remaining > 0; remaining--)
                route.Leave();
        }
        await Task.WhenAll(drained, repeated).WaitAsync(TimeSpan.FromSeconds(5));
        if (route.IsOpenOn(owner) || route.TryEnter(owner) || !route.Retire().IsCompletedSuccessfully)
            throw new InvalidOperationException("All appends have finished; the retired route never reopens.");
    }

    [Test]
    public async Task ConcurrentRetirementAndLastAppendCompleteTheDrain()
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var owner = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "prepared", input.Reader, output.Writer, RpcSessionTestFixture.ClientOptions());

        var route = new NStudyClientRoute(owner, default);
        if (!route.TryEnter(owner))
            throw new InvalidOperationException("The ready route must admit the append.");
        await Task.WhenAll(Task.Run(route.Retire), Task.Run(route.Leave)).WaitAsync(TimeSpan.FromSeconds(5));
        if (route.TryEnter(owner) || !route.Retire().IsCompletedSuccessfully)
            throw new InvalidOperationException("Relinquishing the last publisher must complete retirement.");
    }
}
