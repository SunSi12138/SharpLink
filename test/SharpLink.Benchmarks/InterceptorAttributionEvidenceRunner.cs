using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Serialization;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

public static class InterceptorAttributionEvidenceRunner
{
    public static void Run()
    {
        const int iterations = 100_000;

        Measure("ClientInvocationContext", iterations,
            () => FormatterServices.GetUninitializedObject(typeof(SharpLinkClientInvocationContext)));
        Measure("ServerInvocationContext", iterations,
            () => FormatterServices.GetUninitializedObject(typeof(SharpLinkServerInvocationContext)));

        var clientInterceptorState = FindNested(typeof(SharpLinkClient), "ClientInterceptorState");

        var unaryType = typeof(SharpLinkClient).GetNestedType(
            "UnaryInterceptorState`2", BindingFlags.NonPublic)!.MakeGenericType(typeof(int), typeof(int));
        Measure("UnaryInterceptorState", iterations,
            () => FormatterServices.GetUninitializedObject(unaryType));

        var clientContinuation = FindNested(clientInterceptorState, "ClientInterceptorContinuation");
        Measure("ClientInterceptorContinuation", iterations,
            () => FormatterServices.GetUninitializedObject(clientContinuation));

        // ClientContinuationState is pooled; it contributes ~0 B per intercepted call after warmup.

        // ServerPipelineFacts is now a value struct, so it contributes no per-call heap object.
        var serverPipeline = FindNested(typeof(SharpLinkServer), "ServerPipelineFacts");

        var serverContinuation = FindNested(serverPipeline, "ServerInterceptorContinuation");
        Measure("ServerInterceptorContinuation", iterations,
            () => FormatterServices.GetUninitializedObject(serverContinuation));

        // ServerContinuationState is pooled; it contributes ~0 B per intercepted call after warmup.
    }

    private static Type FindNested(Type owner, string name)
        => owner.GetNestedType(name, BindingFlags.NonPublic)!;

    private static void Measure(string name, int iterations, Func<object> factory)
    {
        _ = factory();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < iterations; index++)
            _ = factory();
        var elapsed = Stopwatch.GetElapsedTime(started);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        Console.WriteLine(
            $"[InterceptorAttribution] case={name} iterations={iterations} " +
            $"nsPerOp={(elapsed.TotalNanoseconds / iterations):F2} " +
            $"allocatedPerOp={(allocated / (double)iterations):F3}");
    }
}
