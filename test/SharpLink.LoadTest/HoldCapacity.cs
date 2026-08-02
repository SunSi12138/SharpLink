using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.LoadTestBase;

namespace SharpLink.LoadTest;

internal sealed class HoldCapacityProbe
{
    private readonly object _gate = new();
    private TaskCompletionSource _release = CreateRelease();
    private int _generation;
    private int _activeCalls;
    private int _peakActiveCalls;
    private bool _thresholdReleaseScheduled;
    private bool _failsafeReleaseScheduled;

    internal int ActiveCalls => Volatile.Read(ref _activeCalls);
    internal int PeakActiveCalls => Volatile.Read(ref _peakActiveCalls);

    internal int Reset()
    {
        lock (_gate)
        {
            if (_activeCalls != 0)
                throw new InvalidOperationException("The hold probe cannot be reset while calls are active.");

            _generation = checked(_generation + 1);
            _release = CreateRelease();
            _peakActiveCalls = 0;
            _thresholdReleaseScheduled = false;
            _failsafeReleaseScheduled = false;
            return _generation;
        }
    }

    internal async ValueTask HoldAsync(
        int generation,
        int expectedAcceptedCalls,
        int holdDurationMilliseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(expectedAcceptedCalls);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(holdDurationMilliseconds);

        TaskCompletionSource release;
        var scheduleThresholdRelease = false;
        var scheduleFailsafeRelease = false;
        lock (_gate)
        {
            if (generation != _generation)
                throw new InvalidOperationException("The hold probe generation is stale.");

            release = _release;
            var activeCalls = ++_activeCalls;
            if (activeCalls > _peakActiveCalls)
                _peakActiveCalls = activeCalls;

            if (!_failsafeReleaseScheduled)
            {
                _failsafeReleaseScheduled = true;
                scheduleFailsafeRelease = true;
            }
            if (activeCalls >= expectedAcceptedCalls && !_thresholdReleaseScheduled)
            {
                _thresholdReleaseScheduled = true;
                scheduleThresholdRelease = true;
            }
        }

        if (scheduleFailsafeRelease)
        {
            var failsafeDelay = TimeSpan.FromMilliseconds(holdDurationMilliseconds) + TimeSpan.FromSeconds(30);
            _ = ReleaseAfterAsync(generation, release, failsafeDelay);
        }
        if (scheduleThresholdRelease)
        {
            _ = ReleaseAfterAsync(
                generation,
                release,
                TimeSpan.FromMilliseconds(holdDurationMilliseconds));
        }

        try
        {
            await release.Task.ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _activeCalls);
        }
    }

    private async Task ReleaseAfterAsync(
        int generation,
        TaskCompletionSource release,
        TimeSpan delay)
    {
        await Task.Delay(delay).ConfigureAwait(false);
        lock (_gate)
        {
            if (generation == _generation && ReferenceEquals(release, _release))
                release.TrySetResult();
        }
    }

    private static TaskCompletionSource CreateRelease()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal static class HoldCapacityRunner
{
    internal static async Task<HoldCapacityResult> RunAsync(
        LoadTestOptions options,
        ISharpLinkClient? clientOverride)
    {
        var clients = new ISharpLinkClient[options.ClientCount];
        var ownedClients = new List<ISharpLinkClient>(options.ClientCount);
        if (clientOverride is not null)
            clients[0] = clientOverride;

        try
        {
            for (var index = clientOverride is null ? 0 : 1; index < clients.Length; index++)
            {
                var client = CreateClient(options);
                clients[index] = client;
                ownedClients.Add(client);
            }

            await Task.WhenAll(clients.Select(static client => client.ConnectAsync().AsTask()));
            var services = clients.Select(static client => client.Get<ILoadTestService>()).ToArray();
            var generation = await services[0].ResetHoldProbeAsync();
            var attemptedCalls = checked(options.ClientCount * options.ConcurrencyPerClient);
            var connectionCapacity = (long)options.ClientCount *
                                     options.MaxConnections *
                                     options.MaxConcurrentCallsPerConnection;
            var pendingCapacity = (long)options.ClientCount *
                                  options.MaxConnections *
                                  options.MaxPendingRequestsPerConnection;
            var expectedAcceptedCalls = checked((int)Math.Min(
                attemptedCalls,
                Math.Min(options.MaxConcurrentCallsPerServer, Math.Min(connectionCapacity, pendingCapacity))));
            var holdDurationMilliseconds = checked(options.HoldDurationSeconds * 1000);
            var calls = new Task[attemptedCalls];
            var callIndex = 0;
            foreach (var service in services)
            {
                for (var index = 0; index < options.ConcurrencyPerClient; index++)
                {
                    calls[callIndex++] = service
                        .HoldAsync(generation, expectedAcceptedCalls, holdDurationMilliseconds)
                        .AsTask();
                }
            }

            var allCalls = Task.WhenAll(calls);
            try
            {
                await allCalls.WaitAsync(TimeSpan.FromSeconds(options.HoldDurationSeconds + 60));
            }
            catch when (allCalls.IsCompleted)
            {
                // Individual failures are classified below after every call has reached a terminal state.
            }

            if (!allCalls.IsCompleted)
                throw new TimeoutException("Hold-capacity calls did not finish within the release grace window.");

            var failures = new FailureRecorder();
            var completedCalls = 0;
            var resourceExhaustedCalls = 0;
            var cancelledCalls = 0;
            var otherFailedCalls = 0;
            var resourceExhaustedReasons = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var call in calls)
            {
                if (call.Status == TaskStatus.RanToCompletion)
                {
                    completedCalls++;
                    continue;
                }
                if (call.IsCanceled)
                {
                    cancelledCalls++;
                    continue;
                }

                var exception = call.Exception?.GetBaseException() ??
                                new InvalidOperationException("A hold call faulted without an exception.");
                if (exception is SharpLinkException { Code: SharpLinkErrorCode.ResourceExhausted })
                {
                    resourceExhaustedCalls++;
                    var reason = ClassifyResourceExhaustion(exception.Message);
                    resourceExhaustedReasons.TryGetValue(reason, out var reasonCount);
                    resourceExhaustedReasons[reason] = reasonCount + 1;
                }
                else
                    otherFailedCalls++;
                failures.Record(exception);
            }

            var activeCallsAfterRelease = await services[0].GetHoldActiveCallsAsync();
            var peakActiveCalls = await services[0].GetHoldPeakActiveCallsAsync();
            var healthyCallsAfterRelease = 0;
            foreach (var service in services)
            {
                try
                {
                    await service.PingAsync();
                    healthyCallsAfterRelease++;
                }
                catch (Exception exception)
                {
                    failures.Record(exception);
                }
            }

            var result = new HoldCapacityResult(
                options.ClientCount,
                checked(options.ClientCount * options.MaxConnections),
                attemptedCalls,
                peakActiveCalls,
                peakActiveCalls,
                completedCalls,
                resourceExhaustedCalls,
                cancelledCalls,
                otherFailedCalls,
                activeCallsAfterRelease,
                healthyCallsAfterRelease,
                options.MaxConcurrentCallsPerConnection,
                options.MaxConcurrentCallsPerServer,
                options.MaxPendingRequestsPerConnection,
                Environment.ProcessorCount,
                RuntimeInformation.FrameworkDescription,
                GCSettings.IsServerGC ? "server" : "workstation",
                options.Transport.ToString(),
                options.PerformanceProfile.ToString(),
                string.Join(
                    ", ",
                    resourceExhaustedReasons
                        .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                        .Select(static pair => $"{pair.Key}:{pair.Value}")),
                failures.Top(5));

            Print(result);
            if (result.ActiveCallsAfterRelease != 0)
                throw new InvalidOperationException("The server hold probe did not return to zero active calls.");
            if (result.HealthyCallsAfterRelease != options.ClientCount)
                throw new InvalidOperationException("At least one client connection was not healthy after capacity release.");
            return result;
        }
        finally
        {
            foreach (var client in ownedClients)
                await client.DisposeAsync();
        }
    }

    private static ISharpLinkClient CreateClient(LoadTestOptions options)
        => LoadTestTransportFactory.CreateClient(
            options.Transport,
            options.Host,
            options.Port,
            options.UdsPath,
            options.PipeName,
            options.HeartbeatIntervalSeconds,
            options.HeartbeatTimeoutSeconds,
            options.MinConnections,
            options.MaxConnections,
            options.PerformanceProfile,
            options.DisableRequestTimeout,
            options.RequestTimeout,
            options.SharedMemoryName,
            options.SharedMemoryCapacity,
            options.SharedMemorySpinCount,
            runtime => Program.ConfigureRuntime(runtime, options));

    private static string ClassifyResourceExhaustion(string message)
    {
        foreach (var reason in new[]
                 {
                     "server_call_capacity",
                     "per_connection_call_capacity",
                     "admission_concurrency",
                     "admission_queue",
                     "pending_request_capacity",
                     "send_queue_capacity"
                 })
        {
            if (message.Contains(reason, StringComparison.Ordinal))
                return reason;
        }
        return "unspecified";
    }

    private static void Print(HoldCapacityResult result)
    {
        Console.WriteLine("[HoldCapacity]");
        Console.WriteLine($"client_count: {result.ClientCount}");
        Console.WriteLine($"connection_count: {result.ConnectionCount}");
        Console.WriteLine($"attempted_calls: {result.AttemptedCalls}");
        Console.WriteLine($"accepted_calls: {result.AcceptedCalls}");
        Console.WriteLine($"peak_active_calls: {result.PeakActiveCalls}");
        Console.WriteLine($"completed_calls: {result.CompletedCalls}");
        Console.WriteLine($"resource_exhausted_calls: {result.ResourceExhaustedCalls}");
        Console.WriteLine($"resource_exhausted_reasons: {result.ResourceExhaustedReasons}");
        Console.WriteLine($"cancelled_calls: {result.CancelledCalls}");
        Console.WriteLine($"other_failed_calls: {result.OtherFailedCalls}");
        Console.WriteLine($"active_calls_after_release: {result.ActiveCallsAfterRelease}");
        Console.WriteLine($"healthy_calls_after_release: {result.HealthyCallsAfterRelease}");
        Console.WriteLine($"max_concurrent_calls_per_connection: {result.MaxConcurrentCallsPerConnection}");
        Console.WriteLine($"max_concurrent_calls_per_server: {result.MaxConcurrentCallsPerServer}");
        Console.WriteLine($"max_pending_requests_per_connection: {result.MaxPendingRequestsPerConnection}");
        Console.WriteLine($"processor_count: {result.ProcessorCount}");
        Console.WriteLine($"runtime: {result.Runtime}");
        Console.WriteLine($"gc: {result.GcMode}");
        Console.WriteLine($"transport: {result.Transport}");
        Console.WriteLine($"profile: {result.Profile}");
        if (!string.IsNullOrEmpty(result.TopFailures))
            Console.WriteLine($"failures: {result.TopFailures}");
    }
}

public sealed record HoldCapacityResult(
    int ClientCount,
    int ConnectionCount,
    int AttemptedCalls,
    int AcceptedCalls,
    int PeakActiveCalls,
    int CompletedCalls,
    int ResourceExhaustedCalls,
    int CancelledCalls,
    int OtherFailedCalls,
    int ActiveCallsAfterRelease,
    int HealthyCallsAfterRelease,
    int MaxConcurrentCallsPerConnection,
    int MaxConcurrentCallsPerServer,
    int MaxPendingRequestsPerConnection,
    int ProcessorCount,
    string Runtime,
    string GcMode,
    string Transport,
    string Profile,
    string ResourceExhaustedReasons,
    string TopFailures);
