// Test/benchmark-only convenience dispatch. This source is not packaged with SharpLink.Runtime.
using System;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Runtime;

internal static class TestOnlyRpcSessionSendExtensions
{
    internal static async ValueTask SendTestStreamChunkAsync<T>(
        this RpcSession session,
        long requestId,
        ushort streamId,
        T item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        var codec = session.RuntimeContext.Codecs.GetCodec<T>();
        if (codec is IRpcSizedCodec<T> sizedCodec &&
            sizedCodec.CanExactSize &&
            sizedCodec.TryGetEncodedSize(item, out var knownEncodedBytes, out var sizedSnapshot))
        {
            await session.SendStreamChunkKnownSizeAsync(
                requestId,
                streamId,
                item,
                sizedCodec,
                knownEncodedBytes,
                sizedSnapshot,
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await session.SendUnsizedStreamChunkAsync(
            requestId,
            streamId,
            item,
            codec,
            cancellationToken).ConfigureAwait(false);
    }
}
