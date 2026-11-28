namespace SharpLink.StreamLoadTest.Tests;

public class StreamLoadInputTests
{
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task EitherOwnerCanCancelAnInput(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        using var producer = new CancellationTokenSource();
        var input = new StreamLoadInput<int>([7, 8], caller.Token);
        await using var enumerator = input.GetAsyncEnumerator(producer.Token);

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current).IsEqualTo(7);
        (cancelCaller ? caller : producer).Cancel();
        await Assert.That(async () => { _ = await enumerator.MoveNextAsync(); })
            .Throws<OperationCanceledException>();
        await Assert.That((cancelCaller ? producer : caller).IsCancellationRequested).IsFalse();
    }
}
