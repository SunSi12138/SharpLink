using System;
using SharpLink.Client;

namespace SharpLink.Benchmarks;

internal sealed class BenchmarkPendingCallOwner : IPendingCallOwner
{
    internal static BenchmarkPendingCallOwner Instance { get; } = new();

    public void OnPendingCallRegistered()
    {
    }

    public void OnPendingCallCompleted(in PendingCallCompletion completion)
    {
    }

    public void OnProducerCancellationCallbackFailed(Exception exception)
    {
    }
}
