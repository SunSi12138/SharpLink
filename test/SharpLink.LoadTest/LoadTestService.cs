using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json.Serialization;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.LoadTestBase;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.LoadTest;

[RpcContract]
public interface ILoadTestService : IService
{
    [NonCancellable]
    ValueTask PingAsync();
    [NonCancellable]
    ValueTask<int> AddAsync(int left, int right);
    [NonCancellable]
    ValueTask<string> EchoAsync(string value);
    [NonCancellable]
    ValueTask<int> YieldAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> DelayAsync(int left, int right);
    [Oneway]
    [NonCancellable]
    ValueTask NotifyAsync(int left, int right);
    [NonCancellable]
    ValueTask<int> ResetHoldProbeAsync();
    [NonCancellable]
    ValueTask HoldAsync(int generation, int expectedAcceptedCalls, int holdDurationMilliseconds);
    [NonCancellable]
    ValueTask<int> GetHoldActiveCallsAsync();
    [NonCancellable]
    ValueTask<int> GetHoldPeakActiveCallsAsync();
    [NonCancellable]
    ValueTask<string> GetSessionIdAsync();
}

[RpcService]
public class LoadTestService : ILoadTestService
{
    private readonly HoldCapacityProbe _holdProbe = new();

    public ValueTask PingAsync() => ValueTask.CompletedTask;
    public ValueTask<int> AddAsync(int left, int right) => ValueTask.FromResult(left + right);
    public ValueTask<string> EchoAsync(string value) => ValueTask.FromResult(value);

    public async ValueTask<int> YieldAsync(int left, int right)
    {
        await Task.Yield();
        return left + right;
    }

    public async ValueTask<int> DelayAsync(int left, int right)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(1)).ConfigureAwait(false);
        return left + right;
    }

    public ValueTask NotifyAsync(int left, int right) => ValueTask.CompletedTask;

    public ValueTask<int> ResetHoldProbeAsync() => ValueTask.FromResult(_holdProbe.Reset());

    public ValueTask HoldAsync(int generation, int expectedAcceptedCalls, int holdDurationMilliseconds)
        => _holdProbe.HoldAsync(generation, expectedAcceptedCalls, holdDurationMilliseconds);

    public ValueTask<int> GetHoldActiveCallsAsync()
        => ValueTask.FromResult(_holdProbe.ActiveCalls);

    public ValueTask<int> GetHoldPeakActiveCallsAsync()
        => ValueTask.FromResult(_holdProbe.PeakActiveCalls);

    public ValueTask<string> GetSessionIdAsync()
        => ValueTask.FromResult(
            SharpLinkCallContext.Current?.SessionId ??
            throw new InvalidOperationException("The current RPC call has no server session identity."));
}
