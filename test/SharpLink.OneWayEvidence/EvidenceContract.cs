using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.OneWayEvidence;

[RpcContract]
public interface IOneWayEvidence : IService
{
    [Oneway]
    [SharpLink.Sdk.Timeout]
    ValueTask ZeroAsync(long callId, CancellationToken cancellationToken = default);
    [Oneway]
    [SharpLink.Sdk.Timeout]
    ValueTask OneAsync(long callId, IAsyncEnumerable<int> values, CancellationToken cancellationToken = default);
    [Oneway]
    [SharpLink.Sdk.Timeout]
    ValueTask TwoAsync(long callId, IAsyncEnumerable<int> left, IAsyncEnumerable<int> right, CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask LayoutEmptyAsync(CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask LayoutReferenceAsync(string text, CancellationToken cancellationToken = default);
    [Oneway]
    ValueTask LayoutWideAsync(long a, long b, long c, long d, CancellationToken cancellationToken = default);
}

[RpcService]
public sealed class EvidenceService : IOneWayEvidence
{
    internal CompletionBarrier Barrier { get; set; } = null!;
    internal int ItemCount { get; set; }

    // Layout-only fixtures. The timed full-RPC workload uses the ID-bearing methods.
    public ValueTask LayoutEmptyAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask LayoutReferenceAsync(string text, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    public ValueTask LayoutWideAsync(long a, long b, long c, long d, CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

    public ValueTask ZeroAsync(long callId, CancellationToken cancellationToken = default)
    {
        Barrier.Complete(callId, null, 0);
        return ValueTask.CompletedTask;
    }

    public async ValueTask OneAsync(long callId, IAsyncEnumerable<int> values, CancellationToken cancellationToken = default)
    {
        try
        {
            await ValidateAsync(values, callId, 0).ConfigureAwait(false);
            Barrier.Complete(callId, null, ItemCount);
        }
        catch (Exception error)
        {
            Barrier.Complete(callId, error, 0);
        }
    }

    public async ValueTask TwoAsync(long callId, IAsyncEnumerable<int> left, IAsyncEnumerable<int> right, CancellationToken cancellationToken = default)
    {
        try
        {
            await ValidateAsync(left, callId, 0).ConfigureAwait(false);
            await ValidateAsync(right, callId, 1).ConfigureAwait(false);
            Barrier.Complete(callId, null, 2 * ItemCount);
        }
        catch (Exception error)
        {
            Barrier.Complete(callId, error, 0);
        }
    }

    private async ValueTask ValidateAsync(IAsyncEnumerable<int> values, long callId, int stream)
    {
        var index = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            if (index >= ItemCount || value != Expected(callId, stream, index))
                throw new InvalidOperationException($"Invalid item: call={callId}, stream={stream}, index={index}, value={value}.");
            index++;
        }
        if (index != ItemCount)
            throw new InvalidOperationException($"Truncated stream: call={callId}, stream={stream}, items={index}.");
    }

    internal static int Expected(long callId, int stream, int item)
        => unchecked((int)(callId * 31 + stream * 101 + item));

    internal static async IAsyncEnumerable<int> Items(long callId, int stream, int count)
    {
        for (var index = 0; index < count; index++)
            yield return Expected(callId, stream, index);
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
