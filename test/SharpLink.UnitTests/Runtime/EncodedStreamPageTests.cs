namespace SharpLink.UnitTests.Runtime;

public class EncodedStreamPageTests
{
    [Test]
    public void ProducerReleaseShouldLeaveEveryPublishedFrameAlive()
    {
        var buffer = new byte[4096];
        var returns = 0;
        var page = new EncodedStreamPage(buffer, returned =>
        {
            Ensure(ReferenceEquals(buffer, returned), "return must preserve the rented array identity");
            Interlocked.Increment(ref returns);
        });
        page.AddRef();
        page.AddRef();
        page.Release();
        Ensure(returns == 0 && ReferenceEquals(page.Buffer, buffer),
            "ending production must not return storage still owned by published frames");
        page.Release();
        Ensure(returns == 0 && page.Capacity == buffer.Length,
            "one remaining physical frame must retain the whole page");
        page.Release();
        Ensure(returns == 1, "the last owner must return the page exactly once");
    }

    [Test]
    public void ReturnedPageMustNotBeResurrectedOrRead()
    {
        var returns = 0;
        var page = new EncodedStreamPage(new byte[4096], _ => returns++);
        page.Release();
        ExpectInvalidLifetime(() => page.AddRef());
        ExpectInvalidLifetime(() => { _ = page.Buffer; });
        Ensure(returns == 1, "invalid access must not return a page a second time");
    }

    [Test]
    public async Task ConcurrentPhysicalReleasesShouldReturnExactlyOnce()
    {
        const int frameCount = 128;
        var returns = 0;
        var page = new EncodedStreamPage(new byte[4096], _ => Interlocked.Increment(ref returns));
        for (var index = 0; index < frameCount; index++)
            page.AddRef();
        page.Release();
        var releases = new Task[frameCount];
        for (var index = 0; index < frameCount; index++)
            releases[index] = Task.Run(page.Release);
        await Task.WhenAll(releases);
        Ensure(returns == 1, "racing final frame releases must return the array exactly once");
        ExpectInvalidLifetime(() => page.AddRef());
    }

    [Test]
    public void ReusedArrayMustHaveFreshOwnerIdentity()
    {
        var buffer = new byte[4096];
        var budget = new EncodedStreamPageBudget(4096, _ => buffer, _ => { });
        var first = budget.TryRent() ?? throw new Exception("first rent was rejected");
        first.Release();
        var second = budget.TryRent() ?? throw new Exception("second rent was rejected");
        try
        {
            Ensure(!ReferenceEquals(first, second),
                "an array reused by the pool must acquire a fresh page generation identity");
            Ensure(ReferenceEquals(second.Buffer, buffer), "this test must actually reuse the underlying array");
            ExpectInvalidLifetime(() => first.AddRef());
            ExpectInvalidLifetime(() => { _ = first.Buffer; });
            Ensure(budget.BytesOwned == buffer.Length, "stale owner access must not affect the new generation");
        }
        finally
        {
            second.Release();
        }
        Ensure(budget.BytesOwned == 0, "fresh generation release must retire its reservation");
    }

    [Test]
    public void BudgetMustChargeActualRentedCapacityAndRetainItUntilLastOwner()
    {
        const int actualBytes = 8192;
        var returns = 0;
        var budget = new EncodedStreamPageBudget(actualBytes, requested =>
        {
            Ensure(requested == EncodedStreamPageBudget.PageBytes, "the target page size must remain 4 KiB");
            return new byte[actualBytes];
        }, _ => returns++);
        var page = budget.TryRent() ?? throw new Exception("first rent was rejected");
        page.AddRef();
        Ensure(budget.BytesOwned == actualBytes, "budget must charge actual pool capacity, not requested bytes");
        Ensure(budget.TryRent() is null, "a full budget must decline admission without waiting");
        page.Release();
        Ensure(budget.BytesOwned == actualBytes && returns == 0,
            "producer release alone must not restore physical page budget");
        page.Release();
        Ensure(budget.BytesOwned == 0 && returns == 1, "last physical release restores the entire actual capacity");
    }

    [Test]
    public void OversizedPoolBucketShouldBeReturnedWithoutExceedingBudget()
    {
        var returns = 0;
        var budget = new EncodedStreamPageBudget(4096, _ => new byte[8192], _ => returns++);
        Ensure(budget.TryRent() is null, "an oversized actual pool bucket must not breach the budget");
        Ensure(budget.BytesOwned == 0 && returns == 1,
            "a rejected bucket must return immediately without reserving capacity");
    }

    [Test]
    public async Task ConcurrentRentAttemptsMustNeverOverbookTheBudget()
    {
        const int maxBytes = 4 * 4096;
        var returns = 0;
        var budget = new EncodedStreamPageBudget(maxBytes, size => new byte[size],
            _ => Interlocked.Increment(ref returns));
        var rents = new Task<EncodedStreamPage?>[32];
        for (var index = 0; index < rents.Length; index++)
            rents[index] = Task.Run(budget.TryRent);
        var pages = await Task.WhenAll(rents);
        var accepted = 0;
        foreach (var page in pages)
        {
            if (page is not null)
                accepted++;
        }
        Ensure(accepted == 4 && budget.BytesOwned == maxBytes,
            "concurrent streams must share one hard session-owned capacity limit");
        foreach (var page in pages)
            page?.Release();
        Ensure(budget.BytesOwned == 0 && returns == accepted,
            "all successful concurrent reservations must be released once");
    }

    [Test]
    public void AppendOnlyWriterMustPreservePublishedSlicesAcrossAbandonAndRotation()
    {
        var returned = 0;
        var budget = new EncodedStreamPageBudget(8192, size => new byte[size], _ => returned++);
        using var writer = new EncodedStreamPageWriter(budget);
        Ensure(writer.TryBeginFrame(4090), "the first frame must fit");
        writer.GetSpan(4090).Fill(0x35);
        writer.Advance(4090);
        var first = writer.CommitFrame();
        Ensure(writer.TryBeginFrame(6), "the final six bytes must fit in the first page");
        writer.GetSpan(6).Fill(0x77);
        writer.Advance(3);
        ExpectInvalidLifetime(() => writer.CommitFrame());
        writer.AbandonFrame();
        Ensure(first.Memory.Span.IndexOfAnyExcept((byte)0x35) < 0,
            "an abandoned following frame must not modify an already-published slice");
        Ensure(writer.TryBeginFrame(7), "an item beyond the remaining tail must rotate to a fresh page");
        writer.GetSpan(7).Fill(0x51);
        writer.Advance(7);
        var second = writer.CommitFrame();
        Ensure(first.Page is not null && second.Page is not null && !ReferenceEquals(first.Page, second.Page),
            "rotated pages must have distinct generation identities");
        Ensure(first.PageOffset == 0 && second.PageOffset == 0,
            "rotation must restart the append cursor without reinterpreting prior slices");
        writer.Dispose();
        Ensure(returned == 0 && budget.BytesOwned == 8192,
            "rotation and producer disposal must preserve both outstanding physical frame leases");
        first.Page!.Release();
        Ensure(returned == 1 && budget.BytesOwned == 4096,
            "releasing one physical page must leave the other page reserved");
        second.Page!.Release();
        Ensure(returned == 2 && budget.BytesOwned == 0, "both physical completions must retire their own pages");
    }

    [Test]
    public void WriterMustNotExposeStorageBeyondItsFixedFrameReservation()
    {
        var budget = new EncodedStreamPageBudget(4096, size => new byte[size], _ => { });
        using var writer = new EncodedStreamPageWriter(budget);
        Ensure(writer.TryBeginFrame(8), "test frame must fit");
        Ensure(writer.GetSpan(1).Length == 8 && writer.GetMemory(1).Length == 8,
            "the trusted codec must see its frame reservation rather than the unwritten page tail");
        writer.Advance(7);
        ExpectInvalidLifetime(() => { _ = writer.GetMemory(2); });
        ExpectInvalidLifetime(() => writer.Advance(2));
        writer.AbandonFrame();
        writer.Dispose();
        Ensure(budget.BytesOwned == 0, "rejected writes must not strand the producer's page reservation");
    }

    private static void ExpectInvalidLifetime(Action action)
    {
        try
        {
            action();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        throw new Exception("A retired page allowed access or resurrection.");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
