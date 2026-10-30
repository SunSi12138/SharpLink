using System.Reflection;
using SharpLink.Abstractions;

internal static class ColdChecks
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    public static async Task<Dictionary<string, bool>> Run(
        Func<int, CancellationToken, IAsyncEnumerable<int>> invoke,
        Func<ValueTask> normalOperation, Type serviceType, Func<int> logicalCount)
    {
        var proof = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["orderedValues"] = true, ["count"] = true
        };
        int Read(string name) => (int)(serviceType.GetField(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new InvalidOperationException($"Missing service counter {name}.")).GetValue(null)!;
        async Task Cleanup(int expected)
        {
            using var deadline = new CancellationTokenSource(Guard);
            while (Read("Completed") != expected || Read("Active") != 0 || logicalCount() != 0)
                await Task.Delay(10, deadline.Token).ConfigureAwait(false);
            if (Read("Started") != expected) throw new InvalidOperationException("Unexpected/missing server producer invocation.");
        }
        async Task Error(int scenario, int expectedItems, SharpLinkErrorCode code, string message)
        {
            int count = 0;
            try
            {
                await foreach (var value in invoke(scenario, CancellationToken.None).ConfigureAwait(false))
                {
                    if (value != count) throw new InvalidOperationException("Error-prefix order mismatch.");
                    count++;
                }
                throw new InvalidOperationException("Required remote error was swallowed.");
            }
            catch (SharpLinkException exception) when (exception.Code == code && exception.Message == message)
            {
                if (count != expectedItems) throw new InvalidOperationException("Error-prefix count mismatch.");
            }
        }

        await Error(1, 0, SharpLinkErrorCode.FailedPrecondition, "fixture-before-first-item").WaitAsync(Guard);
        await Cleanup(1);
        proof["errorBeforeFirstItem"] = true;
        await Error(2, 1, SharpLinkErrorCode.FailedPrecondition, "fixture-after-first-item").WaitAsync(Guard);
        await Cleanup(2);
        proof["errorAfterFirstItem"] = true;

        async Task Cancellation()
        {
            using var cancellation = new CancellationTokenSource();
            await using var reader = invoke(3, cancellation.Token).GetAsyncEnumerator();
            if (!await reader.MoveNextAsync() || reader.Current != 0)
                throw new InvalidOperationException("Cancellation scenario missing first item.");
            var move = reader.MoveNextAsync().AsTask();
            cancellation.Cancel();
            try
            {
                await move;
                throw new InvalidOperationException("Cancellation did not terminate pending MoveNext with cancellation.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
        await Cancellation().WaitAsync(Guard);
        await Cleanup(3);
        proof["cancellation"] = true;

        async Task EarlyBreak()
        {
            int seen = 0;
            await foreach (var value in invoke(4, CancellationToken.None).ConfigureAwait(false))
            {
                if (value != 0) throw new InvalidOperationException("Early-break wrong item.");
                seen++;
                break;
            }
            if (seen != 1) throw new InvalidOperationException("Early-break missing item.");
        }
        await EarlyBreak().WaitAsync(Guard);
        await Cleanup(4);
        proof["earlyBreak"] = true;

        async Task ConsumerException()
        {
            var sentinel = new ConsumerFailure();
            try
            {
                await foreach (var value in invoke(5, CancellationToken.None).ConfigureAwait(false))
                {
                    if (value != 0) throw new InvalidOperationException("Consumer-exception wrong item.");
                    throw sentinel;
                }
                throw new InvalidOperationException("Consumer-exception missing item.");
            }
            catch (ConsumerFailure exception) when (ReferenceEquals(exception, sentinel)) { }
        }
        await ConsumerException().WaitAsync(Guard);
        await Cleanup(5);
        proof["consumerException"] = true;

        async Task ExplicitDispose()
        {
            var reader = invoke(6, CancellationToken.None).GetAsyncEnumerator();
            try
            {
                if (!await reader.MoveNextAsync() || reader.Current != 0)
                    throw new InvalidOperationException("Explicit-dispose missing first item.");
            }
            finally { await reader.DisposeAsync(); }
        }
        await ExplicitDispose().WaitAsync(Guard);
        await Cleanup(6);
        proof["explicitDispose"] = true;

        await Error(7, 1, SharpLinkErrorCode.Internal, "Internal service error.").WaitAsync(Guard);
        await Cleanup(7);
        proof["serviceException"] = true;
        await normalOperation().AsTask().WaitAsync(Guard);
        await Cleanup(7);
        proof["postFailureRecovery"] = true;
        proof["serverCleanup"] = true;
        proof["clientCleanup"] = true;
        return proof;
    }

    private sealed class ConsumerFailure : Exception { }
}
