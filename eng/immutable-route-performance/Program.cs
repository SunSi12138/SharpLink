using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using SharpLink.Client;
using SharpLink.Server;
using static SharpLink.UnitTests.Runtime.ImmutableRouteTransportSupport;

namespace ImmutableRoutePerformance;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            var options = Options.Parse(args);
            var evidence = await RunAsync(options);
            Console.WriteLine(JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static async Task<object> RunAsync(Options options)
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(options.TimeoutSeconds));
        using var plugin = CreatePlugin();
        plugin.State.Hold = false; // Explicitly select the non-blocking performance fixture.
        var listener = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTransport(listener).Build();
        if (options.Prepared)
            EnablePreparedExperiment(server);
        var registered = server.RegisterAssembly(plugin.Assembly);
        Ensure(registered.Succeeded, $"Fixture registration failed: {registered.Error}");
        await server.StartAsync().AsTask().WaitAsync(budget.Token);
        var endpoint = (IPEndPoint)listener.LocalEndPoint!;
        await using var client = (SharpLinkClient)SharpClientBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .UseTcp(endpoint.Address.ToString(), endpoint.Port)
            .UseRequestTimeout(TimeSpan.FromSeconds(10)).Build();
        if (options.Prepared)
            EnablePreparedExperiment(client);
        await client.ConnectAsync().AsTask().WaitAsync(budget.Token);

        // Reflection is cold-only and intentionally permits the SAME harness source to build
        // against the exact baseline, where the experimental properties do not exist.
        var unary = options.Prepared ? Prepare(client, Unary) : Unary;
        var oneWay = options.Prepared ? Prepare(client, OneWay) : OneWay;
        var run = MeasureAsync(client, plugin.State, unary, oneWay, options, budget.Token);
        object measured;
        try
        {
            measured = await run.WaitAsync(budget.Token);
        }
        finally
        {
            // Stop closes a blocked sender on timeout. No per-call timeout wrappers, boxing,
            // reflection, forced AsTask conversion or external watchdog process enters the loop.
            await client.StopAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            await server.StopAsync(TimeSpan.Zero).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
            if (!run.IsCompleted)
            {
                try { await run.WaitAsync(TimeSpan.FromSeconds(15)); }
                catch (Exception) when (budget.IsCancellationRequested) { }
            }
        }

        return new
        {
            schema = "immutable-route-transport-evidence-v1",
            mode = options.Prepared ? "prepared" : "ordinary",
            label = options.Label,
            runtime = RuntimeInformation.FrameworkDescription,
            buildSdk = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Single(attribute => attribute.Key == "BuildSdkVersion").Value,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os = RuntimeInformation.OSDescription,
            processorCount = Environment.ProcessorCount,
            descriptorBytes = Unsafe.SizeOf<RpcMethodDescriptor>(),
            stopwatchFrequency = Stopwatch.Frequency,
            gcServer = System.Runtime.GCSettings.IsServerGC,
            tieredCompilation = Environment.GetEnvironmentVariable("DOTNET_TieredCompilation") ?? "runtime-default",
            tieredPgo = Environment.GetEnvironmentVariable("DOTNET_TieredPGO") ?? "runtime-default",
            warmup = options.Warmup,
            requestTimeoutSeconds = 10,
            totalBudgetSeconds = options.TimeoutSeconds,
            fixture = "Loopback TCP, empty request, int32 unary response, dynamic singleton, manual descriptor calls, no callbacks",
            allocationScope = "Whole-process client+server aggregate; not caller-only or exact steady-state framework bytes/op",
            serverCompletionObservation = "OneWay service execution counter; excludes proof that all server lease/transport cleanup finished",
            generatedPreparedProxy = "not_implemented_or_measured",
            measured
        };
    }

    private static async Task<object> MeasureAsync(SharpLinkClient client, ServiceState state,
        RpcMethodDescriptor unary, RpcMethodDescriptor oneWay, Options options, CancellationToken cancellationToken)
    {
        for (var index = 0; index < options.Warmup; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Ensure(await InvokeUnary(client, unary) == state.Marker, "Warmup unary routed incorrectly.");
            await InvokeOneWay(client, oneWay);
        }
        await WaitForExecutionsAsync(state, options.Warmup, cancellationToken);

        var latencyTicks = new long[options.UnaryCount];
        var unaryAllocStart = GC.GetTotalAllocatedBytes(precise: true);
        var unaryStart = Stopwatch.GetTimestamp();
        for (var index = 0; index < options.UnaryCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var start = Stopwatch.GetTimestamp();
            var response = await InvokeUnary(client, unary);
            latencyTicks[index] = Stopwatch.GetTimestamp() - start;
            Ensure(response == state.Marker, "Measured unary routed incorrectly.");
        }
        var unaryTicks = Stopwatch.GetTimestamp() - unaryStart;
        var unaryAllocated = GC.GetTotalAllocatedBytes(precise: true) - unaryAllocStart;
        Array.Sort(latencyTicks);

        var expectedOneWay = checked(Volatile.Read(ref state.OneWayCalls) + options.OneWayCount);
        var oneWayAllocStart = GC.GetTotalAllocatedBytes(precise: true);
        var oneWayStart = Stopwatch.GetTimestamp();
        for (var index = 0; index < options.OneWayCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await InvokeOneWay(client, oneWay);
        }
        var senderTicks = Stopwatch.GetTimestamp() - oneWayStart;
        var acceptedAllocated = GC.GetTotalAllocatedBytes(precise: true) - oneWayAllocStart;
        // A Ping/Unary response is NOT used as a request-processing fence.
        await WaitForExecutionsAsync(state, expectedOneWay, cancellationToken);
        var executedTicks = Stopwatch.GetTimestamp() - oneWayStart;
        var executedAllocated = GC.GetTotalAllocatedBytes(precise: true) - oneWayAllocStart;

        return new
        {
            unary = new
            {
                operations = options.UnaryCount,
                elapsedSeconds = Seconds(unaryTicks),
                operationsPerSecond = options.UnaryCount / Seconds(unaryTicks),
                latencyMicroseconds = new
                {
                    p50 = Percentile(latencyTicks, 0.50), p95 = Percentile(latencyTicks, 0.95),
                    p99 = Percentile(latencyTicks, 0.99), max = latencyTicks[^1] * 1_000_000.0 / Stopwatch.Frequency
                },
                processAllocatedBytes = unaryAllocated,
                processAllocatedBytesPerOperation = (double)unaryAllocated / options.UnaryCount
            },
            oneWay = new
            {
                operations = options.OneWayCount,
                senderAcceptanceSeconds = Seconds(senderTicks),
                senderNanosecondsPerOperation = Seconds(senderTicks) * 1_000_000_000 / options.OneWayCount,
                senderOperationsPerSecond = options.OneWayCount / Seconds(senderTicks),
                serviceExecutionObservedSeconds = Seconds(executedTicks),
                serviceExecutionsPerSecond = options.OneWayCount / Seconds(executedTicks),
                finalExpectedServiceCounter = expectedOneWay,
                finalObservedServiceCounter = Volatile.Read(ref state.OneWayCalls),
                processAllocatedBytesAtSenderAcceptance = acceptedAllocated,
                processAllocatedBytesAtServiceObservation = executedAllocated,
                processAllocatedBytesPerServiceExecution = (double)executedAllocated / options.OneWayCount,
                observationPollingMilliseconds = 1
            }
        };
    }

    private static async Task WaitForExecutionsAsync(ServiceState state, int expected, CancellationToken token)
    {
        while (Volatile.Read(ref state.OneWayCalls) < expected)
            await Task.Delay(1, token);
        Ensure(Volatile.Read(ref state.OneWayCalls) == expected, "Unexpected extra OneWay execution.");
    }

    private static double Seconds(long ticks) => (double)ticks / Stopwatch.Frequency;
    private static double Percentile(long[] sorted, double percentile)
        => sorted[Math.Clamp((int)Math.Ceiling(sorted.Length * percentile) - 1, 0, sorted.Length - 1)] *
           1_000_000.0 / Stopwatch.Frequency;

    private static void EnablePreparedExperiment(object target)
    {
        var property = target.GetType().GetProperty("EnableImmutableRoutesForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new NotSupportedException("Prepared mode requires the candidate build.");
        property.SetValue(target, true);
    }

    private static RpcMethodDescriptor Prepare(SharpLinkClient client, RpcMethodDescriptor logical)
    {
        var method = client.GetType().GetMethod("PrepareMethodForTesting",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new NotSupportedException("Prepared mode requires the candidate build.");
        return (RpcMethodDescriptor)method.Invoke(client, [logical, AssemblyHash])!;
    }

    private sealed record Options(bool Prepared, int Warmup, int UnaryCount, int OneWayCount,
        int TimeoutSeconds, string Label)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            if ((args.Length & 1) != 0)
                throw new ArgumentException("Arguments are --mode ordinary|prepared --warmup N --unary N --oneway N --timeout-seconds N --label TEXT.");
            for (var index = 0; index < args.Length; index += 2)
            {
                if (args[index] is not ("--mode" or "--warmup" or "--unary" or "--oneway" or "--timeout-seconds" or "--label") ||
                    !values.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException($"Unknown or duplicate option {args[index]}.");
            }
            var mode = values.GetValueOrDefault("--mode", "ordinary");
            if (mode is not ("ordinary" or "prepared"))
                throw new ArgumentException("Mode must be ordinary or prepared.");
            int ReadCount(string key, int fallback, int maximum)
            {
                var value = values.TryGetValue(key, out var text) ? int.Parse(text) : fallback;
                if (value <= 0 || value > maximum)
                    throw new ArgumentOutOfRangeException(key, $"Expected 1..{maximum}.");
                return value;
            }
            return new(mode == "prepared", ReadCount("--warmup", 10_000, 1_000_000),
                ReadCount("--unary", 10_000, 1_000_000), ReadCount("--oneway", 100_000, 10_000_000),
                ReadCount("--timeout-seconds", 120, 3600), values.GetValueOrDefault("--label", "unspecified"));
        }
    }
}
