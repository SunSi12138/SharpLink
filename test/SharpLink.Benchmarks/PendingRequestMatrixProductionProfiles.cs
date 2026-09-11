using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Reflection;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Compression.Zstd;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static partial class PendingRequestMatrixEvidenceRunner
{
    private const int ProductionSaturationCapacity = 64;
    private const int ProductionSaturationHeldCalls = 58;
    private const int ProductionSaturationShortOperations = 64;
    private const int ProductionSaturationCancelCalls = 2;
    private const int ProductionSaturationDeadlineCalls = 2;

    private static async Task RunProductionProfileAsync(
        List<object> cells,
        string name,
        bool tls,
        bool compression,
        bool metrics,
        bool retry,
        bool breaker,
        bool admission,
        bool traceAll,
        int concurrency,
        int operationsPerWorker)
    {
        using var certificate = tls ? CreateCertificate("localhost") : null;
        var service = new BenchmarkRpcService();
        var saturationService = name == "feature-heavy" ? new PendingProductionSaturationRpcService() : null;
        var serverBuilder = SharpLinkServerBuilder.Create();
        if (tls)
        {
            serverBuilder.UseTcp(
                0,
                new SslServerAuthenticationOptions { ServerCertificate = certificate },
                IPAddress.Loopback.ToString(),
                tlsHandshakeTimeout: TimeSpan.FromSeconds(3));
        }
        else
        {
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());
        }
        serverBuilder
            .UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10))
            .ReplaceService<IBenchmarkRpc>(service);
        if (saturationService is not null)
            serverBuilder.ReplaceService<IPendingProductionSaturationRpc>(saturationService);
        if (compression)
            serverBuilder.UseRuntime(ConfigureProductionCompression);
        if (admission)
            serverBuilder.UseAdmissionControl(options => options.Global.UseConcurrency(Math.Max(4096, concurrency * 4)));

        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        using var shutdown = new CancellationTokenSource();
        var serverTask = server.RunUntilStoppedAsync(shutdown.Token).AsTask();
        using var metricScope = metrics ? new PendingMetricScope() : null;
        using var clientTrace = traceAll ? FeatureTelemetryScope.ForClient(ClientFeatureScenario.ClientTraceAll) : null;
        using var serverTrace = traceAll ? FeatureTelemetryScope.ForServer(ServerFeatureScenario.ServerTraceAll) : null;

        var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout();
        if (tls)
        {
            clientBuilder.UseTcp(
                IPAddress.Loopback.ToString(),
                port,
                new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = ValidateTestCertificate
                },
                TimeSpan.FromSeconds(3));
        }
        else
        {
            clientBuilder.UseTcp(IPAddress.Loopback.ToString(), port);
        }
        clientBuilder.UseHeartbeat(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10));
        if (saturationService is not null)
            clientBuilder.UseProtocol(options => options.MaxPendingRequestsPerConnection = ProductionSaturationCapacity);
        if (compression)
            clientBuilder.UseRuntime(ConfigureProductionCompression);
        if (retry)
            clientBuilder.UseRetry();
        if (breaker)
        {
            clientBuilder.UseCircuitBreaker(options =>
            {
                options.MinimumThroughput = 4;
                options.FailureRatio = 0.5;
                options.SamplingDuration = TimeSpan.FromSeconds(10);
                options.BreakDuration = TimeSpan.FromSeconds(1);
            });
        }

        var client = clientBuilder.Build();
        try
        {
            await client.ConnectAsync(shutdown.Token).ConfigureAwait(false);
            var rpc = client.Get<IBenchmarkRpc>();
            foreach (var payloadBytes in new[] { 0, 256, 4096 })
            {
                for (var warmup = 0; warmup < 16; warmup++)
                    await InvokeProfileOperationAsync(rpc, payloadBytes).ConfigureAwait(false);
                metricScope?.ResetMeasurementWindow();

                var workerLatencies = new long[concurrency][];
                var failures = new int[concurrency];
                var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var workers = new Task[concurrency];
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                var cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
                var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
                var gen0Before = GC.CollectionCount(0);
                var gen1Before = GC.CollectionCount(1);
                var gen2Before = GC.CollectionCount(2);
                var wallStarted = Stopwatch.GetTimestamp();
                for (var worker = 0; worker < concurrency; worker++)
                {
                    var workerIndex = worker;
                    workers[worker] = Task.Run(async () =>
                    {
                        var latencies = new long[operationsPerWorker];
                        workerLatencies[workerIndex] = latencies;
                        await start.Task.ConfigureAwait(false);
                        for (var iteration = 0; iteration < operationsPerWorker; iteration++)
                        {
                            var operationStarted = Stopwatch.GetTimestamp();
                            try
                            {
                                await InvokeProfileOperationAsync(rpc, payloadBytes).ConfigureAwait(false);
                            }
                            catch
                            {
                                failures[workerIndex]++;
                            }
                            latencies[iteration] = Stopwatch.GetTimestamp() - operationStarted;
                        }
                    });
                }
                start.TrySetResult();
                await Task.WhenAll(workers).ConfigureAwait(false);
                var wallElapsed = Stopwatch.GetTimestamp() - wallStarted;
                process.Refresh();
                var cpuMilliseconds = Math.Max(0, process.TotalProcessorTime.TotalMilliseconds - cpuBefore);
                var allocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
                var total = (long)concurrency * operationsPerWorker;
                var failureCount = failures.Sum();
                Require(failureCount == 0, $"Production profile {name} observed RPC failures.");
                Require(metricScope is null || metricScope.CurrentPending == 0,
                    $"Production profile {name} stranded pending requests.");
                var latency = workerLatencies.SelectMany(static values => values).ToArray();
                cells.Add(new
                {
                    category = "production-profile",
                    profile = name,
                    tls,
                    compression,
                    metrics,
                    retry,
                    breaker,
                    admission,
                    traceAll,
                    payloadBytes,
                    concurrency,
                    operations = total,
                    qps = total / Math.Max(0.000001, ToSeconds(wallElapsed)),
                    cpuMilliseconds,
                    cpuNanosecondsPerCall = cpuMilliseconds * 1_000_000d / total,
                    allocatedBytes,
                    allocatedBytesPerCall = allocatedBytes / (double)total,
                    gen0Collections = GC.CollectionCount(0) - gen0Before,
                    gen1Collections = GC.CollectionCount(1) - gen1Before,
                    gen2Collections = GC.CollectionCount(2) - gen2Before,
                    failures = failureCount,
                    retries = metricScope?.Retries ?? 0,
                    resourceExhausted = metricScope?.ResourceExhausted ?? 0,
                    pendingHighWater = metricScope?.HighWaterPending,
                    pendingAfter = metricScope?.CurrentPending,
                    latencyNanoseconds = TimingStatistics(latency),
                    invariant = true
                });
            }

            if (saturationService is not null)
            {
                Require(metricScope is not null, "Feature-heavy saturation evidence requires metrics.");
                await RunFeatureHeavyProductionSaturationAsync(cells, client, saturationService, metricScope!)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            await shutdown.CancelAsync().ConfigureAwait(false);
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None)).ConfigureAwait(false);
            await server.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task RunFeatureHeavyProductionSaturationAsync(
        List<object> cells,
        ISharpLinkClient client,
        PendingProductionSaturationRpcService service,
        PendingMetricScope metricScope)
    {
        const double deadlineSeconds = 0.75;
        var rpc = client.Get<IPendingProductionSaturationRpc>();
        Require(await rpc.QuickAsync(-1).ConfigureAwait(false) == -1,
            "Feature-heavy saturation session baseline probe returned the wrong result.");
        var sessionBefore = service.LastSessionId;
        Require(!string.IsNullOrWhiteSpace(sessionBefore),
            "Feature-heavy saturation baseline probe did not expose a server session id.");
        metricScope.ResetMeasurementWindow();

        var held = new Task<int>[ProductionSaturationHeldCalls];
        for (var index = 0; index < held.Length; index++)
            held[index] = rpc.HoldAsync(index).AsTask();

        Require(SpinWait.SpinUntil(
                () => service.EnteredCount >= ProductionSaturationHeldCalls &&
                      metricScope.CurrentPending >= ProductionSaturationHeldCalls,
                TimeSpan.FromSeconds(10)),
            "Feature-heavy saturation calls never reached the deterministic held-open barrier.");

        var occupancyAtBarrier = metricScope.CurrentPending;
        var occupancyPercentAtBarrier = occupancyAtBarrier * 100d / ProductionSaturationCapacity;
        Require(occupancyAtBarrier >= ProductionSaturationHeldCalls,
            "Feature-heavy saturation did not reach the requested held-call occupancy.");
        Require(occupancyPercentAtBarrier >= 90d,
            "Feature-heavy saturation did not reach at least 90% actual pending occupancy.");

        var shortLatencies = new long[ProductionSaturationShortOperations];
        var shortSucceeded = new bool[ProductionSaturationShortOperations];
        var shortResourceExhausted = 0;
        var shortOtherFailures = 0;
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var shortTasks = new Task[ProductionSaturationShortOperations];
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        var cpuBefore = process.TotalProcessorTime.TotalMilliseconds;
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: false);
        var wallStarted = Stopwatch.GetTimestamp();
        for (var index = 0; index < shortTasks.Length; index++)
        {
            var operationIndex = index;
            shortTasks[index] = Task.Run(async () =>
            {
                await start.Task.ConfigureAwait(false);
                var operationStarted = Stopwatch.GetTimestamp();
                try
                {
                    var result = await rpc.QuickAsync(operationIndex).ConfigureAwait(false);
                    Require(result == operationIndex, "Feature-heavy saturation quick RPC returned the wrong result.");
                    shortSucceeded[operationIndex] = true;
                }
                catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.ResourceExhausted)
                {
                    Interlocked.Increment(ref shortResourceExhausted);
                }
                catch
                {
                    Interlocked.Increment(ref shortOtherFailures);
                }
                finally
                {
                    shortLatencies[operationIndex] = Stopwatch.GetTimestamp() - operationStarted;
                }
            });
        }
        start.TrySetResult();
        await Task.WhenAll(shortTasks).ConfigureAwait(false);
        var shortWallElapsed = Stopwatch.GetTimestamp() - wallStarted;
        process.Refresh();
        var shortCpuMilliseconds = Math.Max(0, process.TotalProcessorTime.TotalMilliseconds - cpuBefore);
        var shortAllocatedBytes = Math.Max(0, GC.GetTotalAllocatedBytes(precise: false) - allocatedBefore);
        var shortSuccessCount = shortSucceeded.Count(static value => value);
        Require(shortSuccessCount > 0, "Feature-heavy saturation produced no successful short RPCs.");
        Require(shortOtherFailures == 0, "Feature-heavy saturation observed an unexpected short-RPC failure.");

        using var cancelA = new CancellationTokenSource();
        using var cancelB = new CancellationTokenSource();
        var cancelTasks = new[]
        {
            rpc.HoldAsync(10_001, cancelA.Token).AsTask(),
            rpc.HoldAsync(10_002, cancelB.Token).AsTask()
        };
        var deadlineTasks = new[]
        {
            rpc.HoldWithDeadlineAsync(20_001).AsTask(),
            rpc.HoldWithDeadlineAsync(20_002).AsTask()
        };

        var mixedTarget = ProductionSaturationHeldCalls + ProductionSaturationCancelCalls + ProductionSaturationDeadlineCalls;
        Require(SpinWait.SpinUntil(
                () => service.EnteredCount >= mixedTarget && metricScope.CurrentPending >= mixedTarget,
                TimeSpan.FromSeconds(10)),
            "Feature-heavy saturation cancellation/deadline calls did not enter while occupancy was high.");

        cancelA.Cancel();
        cancelB.Cancel();
        var cancelled = 0;
        var cancelUnexpected = 0;
        foreach (var task in cancelTasks)
        {
            try
            {
                _ = await task.ConfigureAwait(false);
                cancelUnexpected++;
            }
            catch (OperationCanceledException)
            {
                cancelled++;
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.Cancelled)
            {
                cancelled++;
            }
            catch
            {
                cancelUnexpected++;
            }
        }

        var deadlineExceeded = 0;
        var deadlineUnexpected = 0;
        foreach (var task in deadlineTasks)
        {
            try
            {
                _ = await task.ConfigureAwait(false);
                deadlineUnexpected++;
            }
            catch (SharpLinkException ex) when (ex.Code == SharpLinkErrorCode.DeadlineExceeded)
            {
                deadlineExceeded++;
            }
            catch
            {
                deadlineUnexpected++;
            }
        }

        Require(cancelled == ProductionSaturationCancelCalls && cancelUnexpected == 0,
            "Feature-heavy saturation did not preserve the cancellation terminal distribution.");
        Require(deadlineExceeded == ProductionSaturationDeadlineCalls && deadlineUnexpected == 0,
            "Feature-heavy saturation did not preserve the deadline terminal distribution.");
        Require(SpinWait.SpinUntil(
                () => service.EnteredCount == ProductionSaturationHeldCalls &&
                      metricScope.CurrentPending == ProductionSaturationHeldCalls,
                TimeSpan.FromSeconds(10)),
            "Feature-heavy saturation terminal mix did not return to the held-call baseline.");

        var pendingHighWater = metricScope.HighWaterPending;
        var retries = metricScope.Retries;
        var resourceExhaustedMetric = metricScope.ResourceExhausted;
        service.ReleaseAll();
        var heldResults = await Task.WhenAll(held).ConfigureAwait(false);
        for (var index = 0; index < heldResults.Length; index++)
            Require(heldResults[index] == index, "Feature-heavy saturation held RPC returned the wrong result.");

        Require(SpinWait.SpinUntil(
                () => service.EnteredCount == 0 && metricScope.CurrentPending == 0,
                TimeSpan.FromSeconds(10)),
            "Feature-heavy saturation stranded pending state after controlled release.");
        Require(await rpc.QuickAsync(42).ConfigureAwait(false) == 42,
            "Feature-heavy saturation recovery probe returned the wrong result.");
        var sessionAfter = service.LastSessionId;
        var sessionReuse = string.Equals(sessionBefore, sessionAfter, StringComparison.Ordinal);
        Require(sessionReuse,
            $"Feature-heavy saturation changed physical session during recovery: before={sessionBefore}, after={sessionAfter}.");
        Require(metricScope.CurrentPending == 0,
            "Feature-heavy saturation reuse probe stranded pending state.");

        var successfulShortLatencies = shortLatencies
            .Where((_, index) => shortSucceeded[index])
            .ToArray();
        cells.Add(new
        {
            category = "production-profile",
            profile = "feature-heavy-saturation",
            tls = true,
            compression = true,
            metrics = true,
            retry = true,
            breaker = true,
            admission = true,
            traceAll = true,
            capacity = ProductionSaturationCapacity,
            requestedOccupancyPercent = 90,
            heldCalls = ProductionSaturationHeldCalls,
            occupancyAtBarrier,
            occupancyPercentAtBarrier,
            pendingHighWater,
            pendingHighWaterPercent = pendingHighWater * 100d / ProductionSaturationCapacity,
            shortOperations = ProductionSaturationShortOperations,
            shortSuccess = shortSuccessCount,
            shortResourceExhausted,
            shortOtherFailures,
            shortQps = ProductionSaturationShortOperations / Math.Max(0.000001, ToSeconds(shortWallElapsed)),
            shortCpuMilliseconds,
            shortCpuNanosecondsPerOperation = shortCpuMilliseconds * 1_000_000d / ProductionSaturationShortOperations,
            shortAllocatedBytes,
            shortAllocatedBytesPerOperation = shortAllocatedBytes / (double)ProductionSaturationShortOperations,
            shortTerminalLatencyNanoseconds = TimingStatistics(shortLatencies),
            shortSuccessLatencyNanoseconds = TimingStatistics(successfulShortLatencies),
            cancellationRequested = ProductionSaturationCancelCalls,
            cancelled,
            deadlineRequested = ProductionSaturationDeadlineCalls,
            deadlineExceeded,
            deadlineSeconds,
            retries,
            resourceExhaustedMetric,
            pendingAfter = metricScope.CurrentPending,
            sessionBefore,
            sessionAfter,
            sessionReuse,
            invariant = true
        });
    }

    private static async ValueTask InvokeProfileOperationAsync(IBenchmarkRpc rpc, int payloadBytes)
    {
        if (payloadBytes == 0)
        {
            var result = await rpc.AddAsync(20, 22).ConfigureAwait(false);
            Require(result == 42, "Production profile Add returned the wrong result.");
            return;
        }

        var payload = ProfilePayloads.Get(payloadBytes);
        var resultBytes = await rpc.EchoBytesAsync(payload).ConfigureAwait(false);
        Require(resultBytes.Length == payload.Length &&
                resultBytes[0] == payload[0] &&
                resultBytes[^1] == payload[^1],
            "Production profile EchoBytes returned a corrupted payload.");
    }

    private static void ConfigureProductionCompression(SharpLinkRuntimeOptions options)
        => options.Compression.Providers.Add(new SharpLinkZstdCompressionProvider());

    private static X509Certificate2 CreateCertificate(string subjectName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={subjectName}",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(subjectName);
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private static bool ValidateTestCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        _ = sender;
        if (certificate is null)
            return false;
        if ((errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0)
            return false;
        if (chain is null)
            return errors == SslPolicyErrors.None;
        foreach (var status in chain.ChainStatus)
        {
            if (status.Status is X509ChainStatusFlags.UntrustedRoot or X509ChainStatusFlags.PartialChain)
                continue;
            if (status.Status != X509ChainStatusFlags.NoError)
                return false;
        }
        return true;
    }

    private sealed class PendingMetricScope : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _currentPending;
        private long _highWaterPending;
        private long _retries;
        private long _resourceExhausted;

        public PendingMetricScope()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>(OnLongMeasurement);
            _listener.SetMeasurementEventCallback<double>(static (_, _, _, _) => { });
            _listener.Start();
        }

        public long CurrentPending => Volatile.Read(ref _currentPending);
        public long HighWaterPending => Volatile.Read(ref _highWaterPending);
        public long Retries => Volatile.Read(ref _retries);
        public long ResourceExhausted => Volatile.Read(ref _resourceExhausted);

        public void ResetMeasurementWindow()
        {
            if (CurrentPending != 0)
                throw new InvalidOperationException("Cannot reset pending metric window while calls are active.");
            Volatile.Write(ref _highWaterPending, 0);
            Volatile.Write(ref _retries, 0);
            Volatile.Write(ref _resourceExhausted, 0);
        }

        private void OnLongMeasurement(
            Instrument instrument,
            long measurement,
            ReadOnlySpan<KeyValuePair<string, object?>> tags,
            object? state)
        {
            _ = tags;
            _ = state;
            switch (instrument.Name)
            {
                case "sharplink.requests.pending":
                    var current = Interlocked.Add(ref _currentPending, measurement);
                    while (true)
                    {
                        var high = Volatile.Read(ref _highWaterPending);
                        if (current <= high || Interlocked.CompareExchange(ref _highWaterPending, current, high) == high)
                            break;
                    }
                    break;
                case "sharplink.client.retries":
                    Interlocked.Add(ref _retries, measurement);
                    break;
                case "sharplink.resource_exhausted":
                    Interlocked.Add(ref _resourceExhausted, measurement);
                    break;
            }
        }

        public void Dispose() => _listener.Dispose();
    }

    private static class ProfilePayloads
    {
        private static readonly byte[] Payload256 = Create(256);
        private static readonly byte[] Payload4096 = Create(4096);

        public static byte[] Get(int bytes) => bytes switch
        {
            256 => Payload256,
            4096 => Payload4096,
            _ => throw new ArgumentOutOfRangeException(nameof(bytes))
        };

        private static byte[] Create(int length)
        {
            var payload = Enumerable.Repeat((byte)'x', length).ToArray();
            payload[0] = 17;
            payload[^1] = 31;
            return payload;
        }
    }
}

[RpcContract]
public interface IPendingProductionSaturationRpc : IService
{
    [NonCancellable]
    ValueTask<int> QuickAsync(int value);

    ValueTask<int> HoldAsync(int value, CancellationToken cancellationToken = default);

    [Timeout(0.75)]
    ValueTask<int> HoldWithDeadlineAsync(int value, CancellationToken cancellationToken = default);
}

[RpcService]
public sealed class PendingProductionSaturationRpcService : IPendingProductionSaturationRpc
{
    private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _enteredCount;
    private string? _lastSessionId;

    public int EnteredCount => Volatile.Read(ref _enteredCount);
    public string? LastSessionId => Volatile.Read(ref _lastSessionId);

    public ValueTask<int> QuickAsync(int value)
    {
        Volatile.Write(ref _lastSessionId, SharpLinkCallContext.Current?.SessionId);
        return ValueTask.FromResult(value);
    }

    public ValueTask<int> HoldAsync(int value, CancellationToken cancellationToken = default)
        => HoldCoreAsync(value, cancellationToken);

    public ValueTask<int> HoldWithDeadlineAsync(int value, CancellationToken cancellationToken = default)
        => HoldCoreAsync(value, cancellationToken);

    public void ReleaseAll() => _release.TrySetResult();

    private async ValueTask<int> HoldCoreAsync(int value, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _enteredCount);
        try
        {
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return value;
        }
        finally
        {
            Interlocked.Decrement(ref _enteredCount);
        }
    }
}
