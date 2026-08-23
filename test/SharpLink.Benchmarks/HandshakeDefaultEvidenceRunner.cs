using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// Issue #250 evidence runner for choosing a secure default concurrent-handshake bound.
/// It compares a fixed candidate set using real SharpLink TLS + Protocol v2 handshakes in
/// one loopback process, then runs a bounded simultaneous-connect burst to quantify rejection.
/// </summary>
public static class HandshakeDefaultEvidenceRunner
{
    private static readonly int[] SCandidates = [16, 32, 64, 128, 256];
    private static readonly TimeSpan SHandshakeTimeout = TimeSpan.FromSeconds(15);
    private const int SustainedAttempts = 512;
    private const int BurstAttempts = 256;
    private const int WarmupAttempts = 8;

    private static int TcpPort;

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: --handshake-default-evidence <output-json>");

        var outputPath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var admissionGauge = new HandshakeAdmissionGauge();
        var results = new List<HandshakeDefaultCandidateResult>(SCandidates.Length);

        foreach (var candidate in SCandidates)
            results.Add(await RunCandidateAsync(candidate, admissionGauge).ConfigureAwait(false));

        var document = new HandshakeDefaultEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            SustainedAttempts = SustainedAttempts,
            BurstAttempts = BurstAttempts,
            Note = "Server and clients run in one process, so CPU and working-set measurements include both sides. " +
                   "Sustained mode keeps at most the candidate number of healthy full SharpLink connects in flight. " +
                   "Burst mode releases 256 healthy clients together and records the admission peak/rejections. " +
                   "The runner has fixed hard attempt counts and a 15-second handshake timeout.",
            Candidates = results
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<HandshakeDefaultCandidateResult> RunCandidateAsync(
        int candidate,
        HandshakeAdmissionGauge admissionGauge)
    {
        await using var server = StartServer(candidate);

        for (var index = 0; index < WarmupAttempts; index++)
            await ConnectOnceAsync().ConfigureAwait(false);

        await WaitForAdmissionToDrainAsync(admissionGauge).ConfigureAwait(false);
        admissionGauge.ResetPeak();
        var sustainedRejectedBefore = admissionGauge.RejectedTotal;
        var sustained = await MeasureAsync(
            () => RunSustainedAsync(candidate)).ConfigureAwait(false);
        var sustainedRejected = admissionGauge.RejectedTotal - sustainedRejectedBefore;
        var sustainedPeak = admissionGauge.PeakHandshakes;

        await WaitForAdmissionToDrainAsync(admissionGauge).ConfigureAwait(false);
        admissionGauge.ResetPeak();
        var burstRejectedBefore = admissionGauge.RejectedTotal;
        var burst = await MeasureAsync(
            RunBurstAsync).ConfigureAwait(false);
        var burstRejected = admissionGauge.RejectedTotal - burstRejectedBefore;
        var burstPeak = admissionGauge.PeakHandshakes;

        await server.StopAsync().ConfigureAwait(false);
        await WaitForAdmissionToDrainAsync(admissionGauge).ConfigureAwait(false);

        return new HandshakeDefaultCandidateResult
        {
            Candidate = candidate,
            SustainedSuccesses = sustained.Successes,
            SustainedFailures = sustained.Failures,
            SustainedWallMs = sustained.WallMs,
            SustainedCpuMs = sustained.CpuMs,
            SustainedCpuUtilizationPercent = ComputeCpuUtilization(sustained.CpuMs, sustained.WallMs),
            SustainedThroughputPerSecond = sustained.Successes * 1000d / Math.Max(1d, sustained.WallMs),
            SustainedP50Ms = Percentile(sustained.LatenciesMs, 0.50),
            SustainedP95Ms = Percentile(sustained.LatenciesMs, 0.95),
            SustainedP99Ms = Percentile(sustained.LatenciesMs, 0.99),
            SustainedMaxObservedHandshakes = sustainedPeak,
            SustainedAdmissionRejected = sustainedRejected,
            SustainedPeakWorkingSetBytes = sustained.PeakWorkingSetBytes,
            SustainedPeakGcHeapBytes = sustained.PeakGcHeapBytes,
            SustainedPeakThreadCount = sustained.PeakThreadCount,
            BurstSuccesses = burst.Successes,
            BurstFailures = burst.Failures,
            BurstWallMs = burst.WallMs,
            BurstCpuMs = burst.CpuMs,
            BurstCpuUtilizationPercent = ComputeCpuUtilization(burst.CpuMs, burst.WallMs),
            BurstP95Ms = Percentile(burst.LatenciesMs, 0.95),
            BurstP99Ms = Percentile(burst.LatenciesMs, 0.99),
            BurstMaxObservedHandshakes = burstPeak,
            BurstAdmissionRejected = burstRejected,
            BurstPeakWorkingSetBytes = burst.PeakWorkingSetBytes,
            BurstPeakGcHeapBytes = burst.PeakGcHeapBytes,
            BurstPeakThreadCount = burst.PeakThreadCount
        };
    }

    private static async Task<ConnectBatchResult> RunSustainedAsync(int concurrency)
    {
        using var throttle = new SemaphoreSlim(concurrency);
        var latencies = new ConcurrentBag<double>();
        var successes = 0;
        var failures = 0;
        var tasks = new Task[SustainedAttempts];

        for (var index = 0; index < tasks.Length; index++)
            tasks[index] = RunOneAsync();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ConnectBatchResult(successes, failures, latencies.ToArray());

        async Task RunOneAsync()
        {
            await throttle.WaitAsync().ConfigureAwait(false);
            try
            {
                var watch = Stopwatch.StartNew();
                try
                {
                    await ConnectOnceAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref successes);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    watch.Stop();
                    latencies.Add(watch.Elapsed.TotalMilliseconds);
                }
            }
            finally
            {
                throttle.Release();
            }
        }
    }

    private static async Task<ConnectBatchResult> RunBurstAsync()
    {
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latencies = new ConcurrentBag<double>();
        var successes = 0;
        var failures = 0;
        var tasks = new Task[BurstAttempts];

        for (var index = 0; index < tasks.Length; index++)
            tasks[index] = RunOneAsync();

        start.TrySetResult();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return new ConnectBatchResult(successes, failures, latencies.ToArray());

        async Task RunOneAsync()
        {
            await start.Task.ConfigureAwait(false);
            var watch = Stopwatch.StartNew();
            try
            {
                await ConnectOnceAsync().ConfigureAwait(false);
                Interlocked.Increment(ref successes);
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
            finally
            {
                watch.Stop();
                latencies.Add(watch.Elapsed.TotalMilliseconds);
            }
        }
    }

    private static async Task<MeasuredBatchResult> MeasureAsync(
        Func<Task<ConnectBatchResult>> operation)
    {
        var process = Process.GetCurrentProcess();
        process.Refresh();
        var peakWorkingSet = process.WorkingSet64;
        var peakThreadCount = process.Threads.Count;
        var peakGcHeap = GC.GetTotalMemory(forceFullCollection: false);
        using var sampleCts = new CancellationTokenSource();
        var sampleTask = Task.Run(async () =>
        {
            using var sampleProcess = Process.GetCurrentProcess();
            while (!sampleCts.IsCancellationRequested)
            {
                try
                {
                    sampleProcess.Refresh();
                    peakWorkingSet = Math.Max(peakWorkingSet, sampleProcess.WorkingSet64);
                    peakThreadCount = Math.Max(peakThreadCount, sampleProcess.Threads.Count);
                    peakGcHeap = Math.Max(peakGcHeap, GC.GetTotalMemory(forceFullCollection: false));
                    await Task.Delay(10, sampleCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (sampleCts.IsCancellationRequested)
                {
                    break;
                }
            }
        });

        var cpuBefore = process.TotalProcessorTime;
        var watch = Stopwatch.StartNew();
        var batch = await operation().ConfigureAwait(false);
        watch.Stop();
        process.Refresh();
        var cpuAfter = process.TotalProcessorTime;

        sampleCts.Cancel();
        await sampleTask.ConfigureAwait(false);
        peakWorkingSet = Math.Max(peakWorkingSet, process.WorkingSet64);
        peakThreadCount = Math.Max(peakThreadCount, process.Threads.Count);
        peakGcHeap = Math.Max(peakGcHeap, GC.GetTotalMemory(forceFullCollection: false));

        return new MeasuredBatchResult(
            batch.Successes,
            batch.Failures,
            batch.LatenciesMs,
            watch.Elapsed.TotalMilliseconds,
            (cpuAfter - cpuBefore).TotalMilliseconds,
            peakWorkingSet,
            peakGcHeap,
            peakThreadCount);
    }

    private static async Task ConnectOnceAsync()
    {
        var client = SharpClientBuilder.Create()
            .UseTransport(new SocketClientTransportFactory(
                new IPEndPoint(IPAddress.Loopback, TcpPort),
                tlsOptions: CreateClientTlsOptions(),
                tlsHandshakeTimeout: SHandshakeTimeout))
            .UseProtocol(options => options.HandshakeTimeout = SHandshakeTimeout)
            .DisableRequestTimeout()
            .Build();
        try
        {
            await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static ServerHarness StartServer(int maxConcurrentHandshakes)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(
                0,
                CreateServerTlsOptions(),
                backlog: 2048,
                tlsHandshakeTimeout: SHandshakeTimeout)
            .UseProtocol(options => options.HandshakeTimeout = SHandshakeTimeout)
            .UseConnectionAdmission(options =>
            {
                options.MaxConcurrentConnections = 1024;
                options.MaxConcurrentHandshakes = maxConcurrentHandshakes;
            });

        TcpPort = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
        var server = builder.Build();
        var runCts = new CancellationTokenSource();
        var runTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(runCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (runCts.IsCancellationRequested)
            {
            }
        }, runCts.Token);

        if (!SpinWait.SpinUntil(
                () => server.HealthStatus == SharpLinkHealthStatus.Ready,
                TimeSpan.FromSeconds(5)))
        {
            throw new InvalidOperationException("Server did not become Ready within 5 seconds.");
        }
        return new ServerHarness(server, runTask, runCts);
    }

    private static async Task WaitForAdmissionToDrainAsync(HandshakeAdmissionGauge gauge)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (gauge.CurrentHandshakes != 0 && DateTime.UtcNow < deadline)
            await Task.Yield();
        if (gauge.CurrentHandshakes != 0)
            throw new InvalidOperationException($"Handshake gauge did not return to zero: {gauge.CurrentHandshakes}.");
    }

    private static double ComputeCpuUtilization(double cpuMs, double wallMs)
        => cpuMs * 100d / Math.Max(1d, wallMs * Environment.ProcessorCount);

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var ordered = values.OrderBy(static value => value).ToArray();
        var index = (int)Math.Ceiling(percentile * ordered.Length) - 1;
        return ordered[Math.Clamp(index, 0, ordered.Length - 1)];
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=sharplink-handshake-default-evidence",
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
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private static SslServerAuthenticationOptions CreateServerTlsOptions()
        => new() { ServerCertificate = CreateCertificate() };

    private static SslClientAuthenticationOptions CreateClientTlsOptions()
        => new()
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        };

    private sealed class ServerHarness : IAsyncDisposable
    {
        private bool _stopped;

        internal ServerHarness(ISharpLinkServer server, Task runTask, CancellationTokenSource runCts)
        {
            Server = server;
            RunTask = runTask;
            RunCts = runCts;
        }

        internal ISharpLinkServer Server { get; }
        internal Task RunTask { get; }
        internal CancellationTokenSource RunCts { get; }

        internal async Task StopAsync()
        {
            if (_stopped)
                return;
            _stopped = true;
            await Server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
            await RunTask.ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                await Server.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            try
            {
                RunCts.Cancel();
            }
            catch
            {
            }
            RunCts.Dispose();
        }
    }

    private sealed class HandshakeAdmissionGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _currentHandshakes;
        private long _peakHandshakes;
        private long _rejected;

        internal HandshakeAdmissionGauge()
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "SharpLink" &&
                    (instrument.Name == "sharplink.connections.handshakes.active" ||
                     instrument.Name == "sharplink.connections.rejected"))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name.Equals(
                        "sharplink.connections.handshakes.active",
                        StringComparison.Ordinal))
                {
                    var current = Interlocked.Add(ref _currentHandshakes, value);
                    UpdatePeak(current);
                }
                else if (instrument.Name.Equals(
                             "sharplink.connections.rejected",
                             StringComparison.Ordinal))
                {
                    Interlocked.Add(ref _rejected, value);
                }
            });
            _listener.Start();
        }

        internal long CurrentHandshakes => Volatile.Read(ref _currentHandshakes);
        internal long PeakHandshakes => Volatile.Read(ref _peakHandshakes);
        internal long RejectedTotal => Volatile.Read(ref _rejected);

        internal void ResetPeak() => Volatile.Write(ref _peakHandshakes, CurrentHandshakes);

        public void Dispose() => _listener.Dispose();

        private void UpdatePeak(long current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _peakHandshakes);
                if (current <= observed)
                    return;
                if (Interlocked.CompareExchange(ref _peakHandshakes, current, observed) == observed)
                    return;
            }
        }
    }

    private sealed record ConnectBatchResult(int Successes, int Failures, double[] LatenciesMs);

    private sealed record MeasuredBatchResult(
        int Successes,
        int Failures,
        double[] LatenciesMs,
        double WallMs,
        double CpuMs,
        long PeakWorkingSetBytes,
        long PeakGcHeapBytes,
        int PeakThreadCount);
}

public sealed class HandshakeDefaultEvidenceDocument
{
    public string Commit { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public int SustainedAttempts { get; set; }
    public int BurstAttempts { get; set; }
    public string Note { get; set; } = string.Empty;
    public IReadOnlyList<HandshakeDefaultCandidateResult> Candidates { get; set; } = [];
}

public sealed class HandshakeDefaultCandidateResult
{
    public int Candidate { get; set; }
    public int SustainedSuccesses { get; set; }
    public int SustainedFailures { get; set; }
    public double SustainedWallMs { get; set; }
    public double SustainedCpuMs { get; set; }
    public double SustainedCpuUtilizationPercent { get; set; }
    public double SustainedThroughputPerSecond { get; set; }
    public double SustainedP50Ms { get; set; }
    public double SustainedP95Ms { get; set; }
    public double SustainedP99Ms { get; set; }
    public long SustainedMaxObservedHandshakes { get; set; }
    public long SustainedAdmissionRejected { get; set; }
    public long SustainedPeakWorkingSetBytes { get; set; }
    public long SustainedPeakGcHeapBytes { get; set; }
    public int SustainedPeakThreadCount { get; set; }
    public int BurstSuccesses { get; set; }
    public int BurstFailures { get; set; }
    public double BurstWallMs { get; set; }
    public double BurstCpuMs { get; set; }
    public double BurstCpuUtilizationPercent { get; set; }
    public double BurstP95Ms { get; set; }
    public double BurstP99Ms { get; set; }
    public long BurstMaxObservedHandshakes { get; set; }
    public long BurstAdmissionRejected { get; set; }
    public long BurstPeakWorkingSetBytes { get; set; }
    public long BurstPeakGcHeapBytes { get; set; }
    public int BurstPeakThreadCount { get; set; }
}
