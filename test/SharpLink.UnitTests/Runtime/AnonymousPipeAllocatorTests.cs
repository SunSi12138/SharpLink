using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public class AnonymousPipeAllocatorTests
{
    [Test]
    public async Task FullOfferQueueShouldFailFastAndDisposeShouldRejectFurtherOffers()
    {
        var transport = new AnonymousPipeServerTransportListener(2);
        try
        {
            _ = await transport.AllocateAsync();
            _ = await transport.AllocateAsync();

            await ExpectSharpLinkError(
                transport.AllocateAsync().AsTask(),
                SharpLinkErrorCode.ResourceExhausted);

            await transport.DisposeAsync();
            await ExpectException<ObjectDisposedException>(transport.AllocateAsync().AsTask());
        }
        finally
        {
            await transport.DisposeAsync();
        }
    }

    [Test]
    public async Task CanceledOfferShouldNotAllocateHandles()
    {
        await using var transport = new AnonymousPipeServerTransportListener(1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await ExpectException<OperationCanceledException>(
            transport.AllocateAsync(cancellation.Token).AsTask());

        _ = await transport.AllocateAsync();
    }

    [Test]
    public async Task CanceledConsumerShouldNotPoisonOfferQueue()
    {
        await using var transport = new AnonymousPipeServerTransportListener(1);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await ExpectException<OperationCanceledException>(
            transport.AcceptAsync(cancellation.Token).AsTask());

        _ = await transport.AllocateAsync();
        var connection = await transport.AcceptAsync();
        await connection.DisposeAsync();
    }

    private static async Task ExpectSharpLinkError(Task task, SharpLinkErrorCode code)
    {
        try
        {
            await task;
            throw new Exception($"expected {code}");
        }
        catch (SharpLinkException ex) when (ex.Code == code)
        {
        }
    }

    private static async Task ExpectException<TException>(Task task) where TException : Exception
    {
        try
        {
            await task;
            throw new Exception($"expected {typeof(TException).Name}");
        }
        catch (TException)
        {
        }
    }
}
