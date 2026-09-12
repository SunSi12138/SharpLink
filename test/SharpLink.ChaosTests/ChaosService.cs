using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ChaosTests;

[RpcContract]
public interface IChaosService : IService
{
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);

    ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken);

    [NonCancellable]
    ValueTask<int> UploadAsync(IAsyncEnumerable<int> values);

    IAsyncEnumerable<int> StreamAsync(int count, CancellationToken cancellationToken);

    [Oneway]
    [NonCancellable]
    ValueTask PublishAsync(int workerId, int iteration);

    [NonCancellable]
    IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values);
}

[RpcService]
public sealed class ChaosService : IChaosService
{
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);

    public async ValueTask DelayAsync(int milliseconds, CancellationToken cancellationToken)
        => await Task.Delay(milliseconds, cancellationToken).ConfigureAwait(false);

    public async ValueTask<int> UploadAsync(IAsyncEnumerable<int> values)
    {
        var sum = 0;
        var count = 0;
        await foreach (var value in values.ConfigureAwait(false))
        {
            sum += value;
            count++;
        }
        if (count != 16)
            throw new InvalidDataException($"Server received only {count}/16 client-stream items.");
        return sum;
    }

    public async IAsyncEnumerable<int> StreamAsync(
        int count,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return index;
            await Task.Yield();
        }
    }

    public ValueTask PublishAsync(int workerId, int iteration)
    {
        _ = workerId;
        _ = iteration;
        return ValueTask.CompletedTask;
    }

    public async IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values)
    {
        await foreach (var value in values.ConfigureAwait(false))
            yield return value * 2;
    }
}
