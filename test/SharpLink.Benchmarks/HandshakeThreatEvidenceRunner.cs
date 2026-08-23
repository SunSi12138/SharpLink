using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.InteropServices;
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
/// Issue #250 threat-model evidence: compares the legacy explicit-zero handshake behavior
/// with the secure default candidate (64) while clients are held inside TLS, Protocol v2,
/// or the application authenticator. The scenarios intentionally keep the server pre-Ready
/// so the handshake slot remains held and its resource envelope can be observed directly.
/// </summary>
public static class HandshakeThreatEvidenceRunner
{
    private static readonly TimeSpan SHandshakeTimeout = TimeSpan.FromMinutes(2);
    private static readonly int[] SConfiguredBounds = [0, 64];
    private const int Attempts = 256;
    private const int MaxConnections = 1024;

    private static int TcpPort;

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: --handshake-threat-evidence <output-json>");

        var outputPath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var gauge = new HandshakeAdmissionGauge();
        var results = new List<HandshakeThreatScenarioResult>();
        foreach (var bound in SConfiguredBounds)
        {
            results.Add(await RunTlsStallAsync(bound, gauge).ConfigureAwait(false));
            results.Add(await RunProtocolStallAsync(bound, gauge).ConfigureAwait(false));
            results.Add(await RunAuthenticatorStallAsync(bound, gauge).ConfigureAwait(false));
        }

        var document = new HandshakeThreatEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            AttemptsPerScenario = Attempts,
            MaxConcurrentConnections = MaxConnections,
            Note = "Server and clients run in one process on the same runner. Configured handshake bound 0 is the documented explicit opt-out and materializes to the 1024 connection bound; 64 is the proposed secure default. " +
                   "TLS stall connects TCP but sends no ClientHello. Protocol stall completes TLS (therefore performs the expensive TLS work) and then sends no Protocol v2 HandshakeRequest. Auth stall completes TLS + Protocol v2 and blocks in the application authenticator. " +
                   "Process peak deltas include both server and local clients, but active-handshake/rejection/auth-concurrency observations are server-side admission evidence. Stop/drain is measured while the stalled handshakes are still held.",
            Scenarios = results
        };

        var json = JsonSerializer.Serialize(document, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        });
        await File.WriteAllTextAsync(outputPath, json).ConfigureAwait(false);
        Console.WriteLine(json);
    }

    private static async Task<HandshakeThreatScenarioResult> RunTlsStallAsync(
        int configuredBound,
        HandshakeAdmissionGauge gauge)
    {
        await EnsureGaugeDrainedAsync(gauge).ConfigureAwait(false);
        gauge.ResetPeak();
        var rejectedBefore = gauge.RejectedTotal;
        await using var server = StartServer(configuredBound, authenticator: null);
        await using var sampler = new ProcessPeakSampler();

        var clients = new ConcurrentBag<TcpClient>();
        var failures = 0L;
        var watch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, Attempts).Select(async _ =>
        {
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, TcpPort).ConfigureAwait(false);
                clients.Add(client);
            }
            catch
            {
                client.Dispose();
                Interlocked.Increment(ref failures);
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        watch.Stop();

        await WaitForExpectedEnvelopeAsync(configuredBound, gauge, rejectedBefore).ConfigureAwait(false);
        var activeAtSample = gauge.CurrentHandshakes;
        var rejected = gauge.RejectedTotal - rejectedBefore;
        var peak = sampler.SnapshotPeak();

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        foreach (var client in clients)
            client.Dispose();
        await Task.Delay(250).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return CreateResult(
            "tls-stall-no-clienthello",
            configuredBound,
            watch.Elapsed.TotalMilliseconds,
            failures,
            sampler.Baseline,
            peak,
            activeAtSample,
            gauge.PeakHandshakes,
            rejected,
            stopMs,
            gauge.CurrentHandshakes,
            authCurrentAtSample: 0,
            authPeak: 0,
            finalAuthCurrent: 0,
            after);
    }

    private static async Task<HandshakeThreatScenarioResult> RunProtocolStallAsync(
        int configuredBound,
        HandshakeAdmissionGauge gauge)
    {
        await EnsureGaugeDrainedAsync(gauge).ConfigureAwait(false);
        gauge.ResetPeak();
        var rejectedBefore = gauge.RejectedTotal;
        await using var server = StartServer(configuredBound, authenticator: null);
        await using var sampler = new ProcessPeakSampler();

        var clients = new ConcurrentBag<StalledTlsConnection>();
        var failures = 0L;
        var watch = Stopwatch.StartNew();
        using var throttle = new SemaphoreSlim(128);
        var tasks = Enumerable.Range(0, Attempts).Select(async _ =>
        {
            await throttle.WaitAsync().ConfigureAwait(false);
            var client = new TcpClient();
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, TcpPort).ConfigureAwait(false);
                var stream = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false,
                    static (_, _, _, _) => true);
                await stream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = "localhost" }).ConfigureAwait(false);
                clients.Add(new StalledTlsConnection(client, stream));
            }
            catch
            {
                client.Dispose();
                Interlocked.Increment(ref failures);
            }
            finally
            {
                throttle.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        watch.Stop();

        await WaitForExpectedEnvelopeAsync(configuredBound, gauge, rejectedBefore).ConfigureAwait(false);
        var activeAtSample = gauge.CurrentHandshakes;
        var rejected = gauge.RejectedTotal - rejectedBefore;
        var peak = sampler.SnapshotPeak();

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        foreach (var client in clients)
            client.Dispose();
        await Task.Delay(250).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return CreateResult(
            "tls-complete-protocol-stall",
            configuredBound,
            watch.Elapsed.TotalMilliseconds,
            failures,
            sampler.Baseline,
            peak,
            activeAtSample,
            gauge.PeakHandshakes,
            rejected,
            stopMs,
            gauge.CurrentHandshakes,
            authCurrentAtSample: 0,
            authPeak: 0,
            finalAuthCurrent: 0,
            after);
    }

    private static async Task<HandshakeThreatScenarioResult> RunAuthenticatorStallAsync(
        int configuredBound,
        HandshakeAdmissionGauge gauge)
    {
        await EnsureGaugeDrainedAsync(gauge).ConfigureAwait(false);
        gauge.ResetPeak();
        var authenticator = new BlockingAuthenticator();
        var rejectedBefore = gauge.RejectedTotal;
        await using var server = StartServer(configuredBound, authenticator);
        await using var sampler = new ProcessPeakSampler();

        var failures = 0L;
        var failureSamples = new ConcurrentQueue<string>();
        var watch = Stopwatch.StartNew();
        var tasks = Enumerable.Range(0, Attempts).Select(_ => ConnectAsync()).ToArray();

        await WaitForExpectedEnvelopeAsync(configuredBound, gauge, rejectedBefore, authenticator)
            .ConfigureAwait(false);
        watch.Stop();
        var activeAtSample = gauge.CurrentHandshakes;
        var rejected = gauge.RejectedTotal - rejectedBefore;
        var authCurrentAtSample = authenticator.Current;
        var authPeak = authenticator.Peak;
        var peak = sampler.SnapshotPeak();

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        authenticator.Release();
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Expected for clients rejected or cancelled by server Stop.
        }
        await Task.Delay(250).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return CreateResult(
            "authenticator-stall",
            configuredBound,
            watch.Elapsed.TotalMilliseconds,
            Volatile.Read(ref failures),
            sampler.Baseline,
            peak,
            activeAtSample,
            gauge.PeakHandshakes,
            rejected,
            stopMs,
            gauge.CurrentHandshakes,
            authCurrentAtSample,
            authPeak,
            authenticator.Current,
            after,
            failureSamples.ToArray());

        async Task ConnectAsync()
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
            catch (Exception exception)
            {
                Interlocked.Increment(ref failures);
                if (failureSamples.Count < 8)
                    failureSamples.Enqueue(exception.Message);
                throw;
            }
            finally
            {
                try
                {
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }
    }

    private static HandshakeThreatScenarioResult CreateResult(
        string scenario,
        int configuredBound,
        double establishMs,
        long clientFailures,
        ProcessSample baseline,
        ProcessSample peak,
        long activeAtSample,
        long peakActive,
        long rejected,
        double stopMs,
        long finalActive,
        long authCurrentAtSample,
        long authPeak,
        long finalAuthCurrent,
        ProcessSample after,
        IReadOnlyList<string>? failureSamples = null)
        => new()
        {
            Scenario = scenario,
            ConfiguredHandshakeBound = configuredBound,
            EffectiveHandshakeBound = configuredBound == 0 ? MaxConnections : configuredBound,
            Attempts = Attempts,
            EstablishMs = establishMs,
            ClientFailures = clientFailures,
            ClientFailureSamples = failureSamples ?? [],
            Baseline = baseline,
            Peak = peak,
            SocketFdDelta = peak.FdCount - baseline.FdCount,
            ThreadDelta = peak.ThreadCount - baseline.ThreadCount,
            WorkingSetDeltaBytes = peak.WorkingSetBytes - baseline.WorkingSetBytes,
            GcHeapDeltaBytes = peak.GcHeapBytes - baseline.GcHeapBytes,
            AllocatedDeltaBytes = peak.TotalAllocatedBytes - baseline.TotalAllocatedBytes,
            CpuDeltaMs = peak.CpuTimeMs - baseline.CpuTimeMs,
            ActiveHandshakesAtSample = activeAtSample,
            PeakActiveHandshakes = peakActive,
            RejectedConnections = rejected,
            AuthenticatorCurrentAtSample = authCurrentAtSample,
            AuthenticatorPeak = authPeak,
            StopMs = stopMs,
            FinalActiveHandshakes = finalActive,
            FinalAuthenticatorCurrent = finalAuthCurrent,
            SocketFdsReturnedToBaseline = after.FdCount <= baseline.FdCount + 2
        };

    private static ServerHarness StartServer(
        int maxConcurrentHandshakes,
        ISharpLinkServerAuthenticator? authenticator)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseTcp(
                0,
                CreateServerTlsOptions(),
                backlog: 2048,
                tlsHandshakeTimeout: SHandshakeTimeout)
            .UseProtocol(options => options.HandshakeTimeout = SHandshakeTimeout)
            .UseHeartbeat(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(20))
            .UseConnectionAdmission(options =>
            {
                options.MaxConcurrentConnections = MaxConnections;
                options.MaxConcurrentHandshakes = maxConcurrentHandshakes;
            });
        if (authenticator is not null)
            builder.UseAuthenticator(authenticator);

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

    private static async Task WaitForExpectedEnvelopeAsync(
        int configuredBound,
        HandshakeAdmissionGauge gauge,
        long rejectedBefore,
        BlockingAuthenticator? authenticator = null)
    {
        var expectedActive = configuredBound == 0 ? Attempts : Math.Min(configuredBound, Attempts);
        var expectedRejected = Attempts - expectedActive;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var rejected = gauge.RejectedTotal - rejectedBefore;
            var authReady = authenticator is null || authenticator.Current >= expectedActive;
            if (gauge.CurrentHandshakes >= expectedActive && rejected >= expectedRejected && authReady)
                return;
            await Task.Delay(10).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"Admission envelope did not settle: configured={configuredBound}, active={gauge.CurrentHandshakes}, " +
            $"peak={gauge.PeakHandshakes}, rejected={gauge.RejectedTotal - rejectedBefore}, " +
            $"auth_current={authenticator?.Current ?? 0}.");
    }

    private static async Task EnsureGaugeDrainedAsync(HandshakeAdmissionGauge gauge)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (gauge.CurrentHandshakes != 0 && DateTime.UtcNow < deadline)
            await Task.Delay(10).ConfigureAwait(false);
        if (gauge.CurrentHandshakes != 0)
            throw new InvalidOperationException($"Handshake gauge did not drain: {gauge.CurrentHandshakes}.");
    }

    private static async Task<double> MeasureStopAsync(ServerHarness server)
    {
        var watch = Stopwatch.StartNew();
        await server.Server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
        await server.RunTask.ConfigureAwait(false);
        watch.Stop();
        return watch.Elapsed.TotalMilliseconds;
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=sharplink-handshake-threat-evidence",
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

    private sealed class BlockingAuthenticator : ISharpLinkServerAuthenticator
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _current;
        private int _peak;

        internal int Current => Volatile.Read(ref _current);
        internal int Peak => Volatile.Read(ref _peak);

        public ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
            SharpLinkAuthenticationRequest request,
            CancellationToken cancellationToken)
            => AwaitAsync(cancellationToken);

        internal void Release() => _release.TrySetResult();

        private async ValueTask<SharpLinkAuthenticationResult> AwaitAsync(CancellationToken cancellationToken)
        {
            var current = Interlocked.Increment(ref _current);
            UpdatePeak(current);
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return SharpLinkAuthenticationResult.Success;
            }
            finally
            {
                Interlocked.Decrement(ref _current);
            }
        }

        private void UpdatePeak(int current)
        {
            while (true)
            {
                var observed = Volatile.Read(ref _peak);
                if (current <= observed)
                    return;
                if (Interlocked.CompareExchange(ref _peak, current, observed) == observed)
                    return;
            }
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

    private sealed class ProcessPeakSampler : IAsyncDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _samplingTask;
        private ProcessSample _peak;

        internal ProcessPeakSampler()
        {
            Baseline = ProcessSample.Capture();
            _peak = Copy(Baseline);
            _samplingTask = Task.Run(SampleAsync);
        }

        internal ProcessSample Baseline { get; }

        internal ProcessSample SnapshotPeak()
        {
            Update(ProcessSample.Capture());
            lock (_gate)
                return Copy(_peak);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _samplingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            _cts.Dispose();
        }

        private async Task SampleAsync()
        {
            while (!_cts.IsCancellationRequested)
            {
                Update(ProcessSample.Capture());
                await Task.Delay(10, _cts.Token).ConfigureAwait(false);
            }
        }

        private void Update(ProcessSample current)
        {
            lock (_gate)
            {
                _peak.FdCount = Math.Max(_peak.FdCount, current.FdCount);
                _peak.ThreadCount = Math.Max(_peak.ThreadCount, current.ThreadCount);
                _peak.WorkingSetBytes = Math.Max(_peak.WorkingSetBytes, current.WorkingSetBytes);
                _peak.GcHeapBytes = Math.Max(_peak.GcHeapBytes, current.GcHeapBytes);
                _peak.TotalAllocatedBytes = Math.Max(_peak.TotalAllocatedBytes, current.TotalAllocatedBytes);
                _peak.CpuTimeMs = Math.Max(_peak.CpuTimeMs, current.CpuTimeMs);
            }
        }

        private static ProcessSample Copy(ProcessSample sample)
            => new()
            {
                FdCount = sample.FdCount,
                ThreadCount = sample.ThreadCount,
                WorkingSetBytes = sample.WorkingSetBytes,
                GcHeapBytes = sample.GcHeapBytes,
                TotalAllocatedBytes = sample.TotalAllocatedBytes,
                CpuTimeMs = sample.CpuTimeMs
            };
    }

    private sealed class StalledTlsConnection : IDisposable
    {
        private readonly TcpClient _client;
        private readonly SslStream _stream;

        internal StalledTlsConnection(TcpClient client, SslStream stream)
        {
            _client = client;
            _stream = stream;
        }

        public void Dispose()
        {
            try
            {
                _stream.Dispose();
            }
            catch
            {
            }
            _client.Dispose();
        }
    }

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

        public async ValueTask DisposeAsync()
        {
            if (!_stopped)
            {
                try
                {
                    await Server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
                }
                catch
                {
                }
            }
            try
            {
                await Server.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
            }
            RunCts.Cancel();
            RunCts.Dispose();
        }
    }
}

public sealed class HandshakeThreatEvidenceDocument
{
    public string Commit { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public int AttemptsPerScenario { get; set; }
    public int MaxConcurrentConnections { get; set; }
    public string Note { get; set; } = string.Empty;
    public IReadOnlyList<HandshakeThreatScenarioResult> Scenarios { get; set; } = [];
}

public sealed class HandshakeThreatScenarioResult
{
    public string Scenario { get; set; } = string.Empty;
    public int ConfiguredHandshakeBound { get; set; }
    public int EffectiveHandshakeBound { get; set; }
    public int Attempts { get; set; }
    public double EstablishMs { get; set; }
    public long ClientFailures { get; set; }
    public IReadOnlyList<string> ClientFailureSamples { get; set; } = [];
    public ProcessSample Baseline { get; set; } = new();
    public ProcessSample Peak { get; set; } = new();
    public long SocketFdDelta { get; set; }
    public long ThreadDelta { get; set; }
    public long WorkingSetDeltaBytes { get; set; }
    public long GcHeapDeltaBytes { get; set; }
    public long AllocatedDeltaBytes { get; set; }
    public double CpuDeltaMs { get; set; }
    public long ActiveHandshakesAtSample { get; set; }
    public long PeakActiveHandshakes { get; set; }
    public long RejectedConnections { get; set; }
    public long AuthenticatorCurrentAtSample { get; set; }
    public long AuthenticatorPeak { get; set; }
    public double StopMs { get; set; }
    public long FinalActiveHandshakes { get; set; }
    public long FinalAuthenticatorCurrent { get; set; }
    public bool SocketFdsReturnedToBaseline { get; set; }
}
