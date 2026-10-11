using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SharpLink.Abstractions;

namespace SharpLink.Benchmarks;

internal sealed class GrantBenchmarkService : IGrantBenchmarkRpc
{
    private readonly int _itemCount;
    private readonly int _encodedBytes;
    private readonly GrantBenchmarkPayload _expected;

    internal GrantBenchmarkService(int itemCount, int targetEncodedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        _itemCount = itemCount;
        _encodedBytes = targetEncodedBytes;
        _expected = GrantBenchmarkPayload.Create(targetEncodedBytes);
    }

    internal string? LastProbeSessionId { get; private set; }

    public ValueTask<int> ProbeAsync()
    {
        LastProbeSessionId = SharpLinkCallContext.Current?.SessionId
            ?? throw new InvalidOperationException("Generated DTO probe did not run inside an RPC session.");
        return ValueTask.FromResult(42);
    }

    public ValueTask<GrantBenchmarkPayload> EchoAsync(GrantBenchmarkPayload value)
    {
        Validate(value);
        return ValueTask.FromResult(value);
    }

    public async ValueTask<long> UploadAsync(IAsyncEnumerable<GrantBenchmarkPayload> values)
    {
        var count = 0;
        long total = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            Validate(value);
            if (++count > _itemCount)
                throw new InvalidDataException("Generated DTO upload produced too many items.");
            total = checked(total + _encodedBytes);
        }
        RequireCount(count);
        return total;
    }

    public async IAsyncEnumerable<GrantBenchmarkPayload> DownloadAsync(int count, int encodedBytes)
    {
        RequireCount(count);
        if (encodedBytes != _encodedBytes)
            throw new InvalidDataException("Generated DTO download encoded-size request mismatch.");
        for (var index = 0; index < count; index++)
        {
            yield return _expected;
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<GrantBenchmarkPayload> DuplexAsync(IAsyncEnumerable<GrantBenchmarkPayload> values)
    {
        var count = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            Validate(value);
            if (++count > _itemCount)
                throw new InvalidDataException("Generated DTO duplex produced too many input items.");
            yield return value;
        }
        RequireCount(count);
    }

    private void Validate(GrantBenchmarkPayload value)
    {
        if (value is null || value.Tag != _expected.Tag ||
            !string.Equals(value.Text, _expected.Text, StringComparison.Ordinal))
            throw new InvalidDataException("Incoming generated DTO failed complete member validation.");
    }

    private void RequireCount(int count)
    {
        if (count != _itemCount)
            throw new InvalidDataException($"Generated DTO item count mismatch: {count} != {_itemCount}.");
    }
}
