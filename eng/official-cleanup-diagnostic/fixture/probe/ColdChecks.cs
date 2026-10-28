using SharpLink.Abstractions;

internal static class ColdChecks
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    public static async Task Run(Func<int, CancellationToken, IAsyncEnumerable<int>> invoke,
        OwnerObserver observer, bool cancellableControl, Dictionary<string, bool> checks)
    {
        async Task Cleanup(int expected)
        {
            observer.Write("cleanup-start", new { expected });
            using var deadline = new CancellationTokenSource(Guard);
            try
            {
                // Original gate: no removed or relaxed condition, no longer timeout.
                while (observer.Completed != expected || observer.Active != 0 || observer.LogicalCount != 0)
                    await Task.Delay(10, deadline.Token).ConfigureAwait(false);
                if (observer.Started != expected)
                    throw new InvalidOperationException("Unexpected/missing server producer invocation.");
                observer.Write("cleanup-pass", new { expected });
            }
            catch (Exception error)
            {
                observer.Write("cleanup-failure", new { expected, error = error.ToString(), timeout = deadline.IsCancellationRequested });
                throw;
            }
        }
        async Task Error(int scenario, int expectedItems, string message)
        {
            observer.Scenario = scenario;
            observer.Write("before-invocation");
            int count = 0;
            try
            {
                await foreach (var value in invoke(scenario, CancellationToken.None).ConfigureAwait(false))
                {
                    if (value != count) throw new InvalidOperationException("Error-prefix order mismatch.");
                    count++;
                    observer.Write("first-item", new { value });
                }
                throw new InvalidOperationException("Required remote error was swallowed.");
            }
            catch (SharpLinkException exception) when (exception.Code == SharpLinkErrorCode.FailedPrecondition && exception.Message == message)
            {
                if (count != expectedItems) throw new InvalidOperationException("Error-prefix count mismatch.");
                observer.Write("expected-error", new { count, code = exception.Code.ToString(), exception.Message });
            }
            observer.Write("after-dispose");
        }
        await Error(1, 0, "fixture-before-first-item").WaitAsync(Guard);
        await Cleanup(1);
        checks["errorBeforeFirstItem"] = true;
        await Error(2, 1, "fixture-after-first-item").WaitAsync(Guard);
        await Cleanup(2);
        checks["errorAfterFirstItem"] = true;

        async Task Cancellation()
        {
            observer.Scenario = 3;
            observer.Write("before-invocation");
            using var cancellation = new CancellationTokenSource();
            await using (var reader = invoke(3, cancellation.Token).GetAsyncEnumerator())
            {
                if (!await reader.MoveNextAsync() || reader.Current != 0)
                    throw new InvalidOperationException("Cancellation scenario missing first item.");
                observer.Write("first-item", new { value = reader.Current });
                var move = reader.MoveNextAsync().AsTask();
                cancellation.Cancel();
                observer.Write("caller-cancelled");
                try
                {
                    await move;
                    throw new InvalidOperationException("Cancellation did not terminate pending MoveNext with cancellation.");
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            }
            observer.Write("after-dispose");
        }
        await Cancellation().WaitAsync(Guard);
        await Cleanup(3);
        checks["cancellation"] = true;

        async Task EarlyBreak()
        {
            observer.Scenario = 4;
            // This CTS exists only for the separately labelled control. Never explicitly cancel.
            using var control = cancellableControl ? new CancellationTokenSource() : null;
            var token = control?.Token ?? CancellationToken.None;
            observer.Write("before-invocation", new { tokenCanBeCanceled = token.CanBeCanceled, explicitlyCancelled = false });
            int seen = 0;
            await foreach (var value in invoke(4, token).ConfigureAwait(false))
            {
                if (value != 0) throw new InvalidOperationException("Early-break wrong item.");
                seen++;
                observer.Write("first-item", new { value });
                break;
            }
            if (seen != 1) throw new InvalidOperationException("Early-break missing item.");
            observer.Write("after-dispose", new { seen, controlExplicitlyCancelled = false,
                controlTokenCancelled = token.IsCancellationRequested });
        }
        await EarlyBreak().WaitAsync(Guard);
        await Cleanup(4);
        checks["earlyBreak"] = true;
    }
}
