using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
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
/// Issue #162 Phase-0 evidence runner: quantifies the framework-owned connection resource
/// envelope of a SharpLink server before any connection-level admission exists. It measures,
/// against real loopback servers, how live accepted/pre-auth connections, framework state,
/// process resources, and Stop/Drain time grow with the connection arrival set.
/// </summary>
public static class ConnectionAdmissionEvidenceRunner
{
    private static readonly TimeSpan SLongHandshakeTimeout = TimeSpan.FromMinutes(5);

    private static int TcpPort;

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: --connection-admission-evidence <output-json>");

        var outputPath = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        using var gauge = new ServerReadyConnectionGauge();
        var results = new List<ConnectionAdmissionScenarioResult>();

        foreach (var count in new[] { 100, 1000, 5000 })
            results.Add(await RunTcpStallNoBytesAsync(count, gauge).ConfigureAwait(false));
        foreach (var count in new[] { 100, 1000, 2000 })
            results.Add(await RunTlsStallHandshakeAsync(count, gauge).ConfigureAwait(false));
        foreach (var count in new[] { 100, 1000, 2000 })
            results.Add(await RunTlsReadyStallProtocolAsync(count, gauge).ConfigureAwait(false));
        foreach (var count in new[] { 100, 500 })
            results.Add(await RunAuthenticationStallAsync(count, gauge).ConfigureAwait(false));
        foreach (var count in new[] { 16, 128 })
            results.Add(await RunReadyConnectionsAsync(count, gauge).ConfigureAwait(false));
        results.Add(await RunTlsHandshakeBurstAsync(gauge).ConfigureAwait(false));

        var document = new ConnectionAdmissionEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Framework = RuntimeInformation.FrameworkDescription,
            Os = RuntimeInformation.OSDescription,
            ProcessorCount = Environment.ProcessorCount,
            Note = "Server and clients run in one probe process. Socket-fd deltas therefore include " +
                   "one client-side socket per connection; the server-side accepted socket is one " +
                   "half of every 2-socket delta. 'sharplink.connections.active' only counts Ready " +
                   "server connections (NotifyConnected), so pre-auth connections are invisible to it.",
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

    // ------------------------------------------------------------------ scenarios

    private static async Task<ConnectionAdmissionScenarioResult> RunTcpStallNoBytesAsync(
        int count,
        ServerReadyConnectionGauge gauge)
    {
        await using var server = StartServer(tls: false, authenticator: null);
        var baseline = ProcessSample.Capture();
        var gaugeBaseline = gauge.ServerConnections;

        var (clients, failures, connectMs) = await OpenConnectionsAsync(
            count,
            static async _ =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, TcpPort).ConfigureAwait(false);
                return (IDisposable)client;
            }).ConfigureAwait(false);
        var peak = await WaitForStableSampleAsync().ConfigureAwait(false);

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        foreach (var client in clients)
            client.Dispose();
        await Task.Delay(300).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return new ConnectionAdmissionScenarioResult
        {
            Scenario = "tcp-stall-nobytes",
            Connections = count,
            ConnectMs = connectMs,
            ConnectFailures = failures,
            Baseline = baseline,
            Peak = peak,
            ReadyConnectionsObserved = gauge.ServerConnections - gaugeBaseline,
            StopMs = stopMs,
            SocketFdsReturnedToBaseline = after.FdCount <= baseline.FdCount + 2
        };
    }

    private static async Task<ConnectionAdmissionScenarioResult> RunTlsStallHandshakeAsync(
        int count,
        ServerReadyConnectionGauge gauge)
    {
        await using var server = StartServer(tls: true, authenticator: null);
        var baseline = ProcessSample.Capture();
        var gaugeBaseline = gauge.ServerConnections;

        var (clients, failures, connectMs) = await OpenConnectionsAsync(
            count,
            static async _ =>
            {
                // Complete TCP connect, then never send the TLS ClientHello:
                // the server parks in AuthenticateAsServerAsync until the TLS timeout.
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, TcpPort).ConfigureAwait(false);
                return (IDisposable)client;
            }).ConfigureAwait(false);
        var peak = await WaitForStableSampleAsync().ConfigureAwait(false);

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        foreach (var client in clients)
            client.Dispose();
        await Task.Delay(300).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return new ConnectionAdmissionScenarioResult
        {
            Scenario = "tls-stall-handshake",
            Connections = count,
            ConnectMs = connectMs,
            ConnectFailures = failures,
            Baseline = baseline,
            Peak = peak,
            ReadyConnectionsObserved = gauge.ServerConnections - gaugeBaseline,
            StopMs = stopMs,
            SocketFdsReturnedToBaseline = after.FdCount <= baseline.FdCount + 2
        };
    }

    private static async Task<ConnectionAdmissionScenarioResult> RunTlsReadyStallProtocolAsync(
        int count,
        ServerReadyConnectionGauge gauge)
    {
        await using var server = StartServer(tls: true, authenticator: null);
        var baseline = ProcessSample.Capture();
        var gaugeBaseline = gauge.ServerConnections;

        var (clients, failures, connectMs) = await OpenConnectionsAsync(
            count,
            static async _ =>
            {
                var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, TcpPort).ConfigureAwait(false);
                var stream = new SslStream(
                    client.GetStream(),
                    leaveInnerStreamOpen: false,
                    static (_, _, _, _) => true);
                await stream.AuthenticateAsClientAsync(
                    new SslClientAuthenticationOptions { TargetHost = "localhost" }).ConfigureAwait(false);
                // TLS completed; never send the Protocol v2 HandshakeRequest.
                // The server now holds a full RpcSession + ServerConnectionState in its live set.
                return new StalledTlsConnection(client, stream);
            }).ConfigureAwait(false);
        var peak = await WaitForStableSampleAsync().ConfigureAwait(false);

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        foreach (var client in clients)
            client.Dispose();
        await Task.Delay(300).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return new ConnectionAdmissionScenarioResult
        {
            Scenario = "tls-ready-stall-protocol",
            Connections = count,
            ConnectMs = connectMs,
            ConnectFailures = failures,
            Baseline = baseline,
            Peak = peak,
            ReadyConnectionsObserved = gauge.ServerConnections - gaugeBaseline,
            StopMs = stopMs,
            SocketFdsReturnedToBaseline = after.FdCount <= baseline.FdCount + 2
        };
    }

    private static async Task<ConnectionAdmissionScenarioResult> RunAuthenticationStallAsync(
        int count,
        ServerReadyConnectionGauge gauge)
    {
        var authenticator = new DelayedServerAuthenticator();
        await using var server = StartServer(tls: true, authenticator: authenticator);
        var baseline = ProcessSample.Capture();
        var gaugeBaseline = gauge.ServerConnections;

        var failures = 0L;
        var failureSamples = new List<string>();
        var connectWatch = Stopwatch.StartNew();
        var connectTasks = new List<Task>();
        var launched = 0;
        while (launched < count)
        {
            var batchSize = Math.Min(64, count - launched);
            for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                connectTasks.Add(ConnectAuthStallClientAsync().ContinueWith(
                    completed =>
                    {
                        if (!completed.IsFaulted)
                            return;
                        Interlocked.Increment(ref failures);
                        var message = completed.Exception?.Flatten().InnerExceptions
                            .Select(static exception => exception.Message).FirstOrDefault() ?? "unknown";
                        lock (failureSamples)
                        {
                            if (failureSamples.Count < 8)
                                failureSamples.Add(message);
                        }
                    },
                    TaskScheduler.Default));
            }
            launched += batchSize;

            // Every connect parks in the server authenticator before its task can complete.
            // Wait until the launched batch has parked server-side, then launch the next batch.
            var batchDeadline = DateTime.UtcNow.AddSeconds(120);
            while (authenticator.Entered < launched && DateTime.UtcNow < batchDeadline)
                await Task.Delay(50).ConfigureAwait(false);
        }
        connectWatch.Stop();
        var parkFailures = Interlocked.Read(ref failures);
        var parkFailureSamples = failureSamples.ToArray();
        var parkedCount = authenticator.Entered;

        var peak = await WaitForStableSampleAsync().ConfigureAwait(false);

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        authenticator.Release();
        try
        {
            await Task.WhenAll(connectTasks).ConfigureAwait(false);
        }
        catch
        {
            // Teardown: server stop already faulted the in-flight connects.
        }
        var after = ProcessSample.Capture();

        return new ConnectionAdmissionScenarioResult
        {
            Scenario = "auth-stall",
            Connections = parkedCount,
            ConnectMs = connectWatch.Elapsed.TotalMilliseconds,
            ConnectFailures = parkFailures,
            ConnectFailureSamples = parkFailureSamples,
            Baseline = baseline,
            Peak = peak,
            ReadyConnectionsObserved = gauge.ServerConnections - gaugeBaseline,
            StopMs = stopMs,
            SocketFdsReturnedToBaseline = after.FdCount <= baseline.FdCount + 2
        };

        Task ConnectAuthStallClientAsync()
        {
            var client = SharpClientBuilder.Create()
                .UseTransport(new SocketClientTransportFactory(
                    new IPEndPoint(IPAddress.Loopback, TcpPort),
                    tlsOptions: CreateClientTlsOptions(),
                    tlsHandshakeTimeout: SLongHandshakeTimeout))
                .UseProtocol(options => options.HandshakeTimeout = SLongHandshakeTimeout)
                .DisableRequestTimeout()
                .Build();
            // Held in flight: TLS + Protocol handshake complete, then the server's
            // authenticator parks the connection until teardown.
            return AwaitAndDisposeAsync();

            async Task AwaitAndDisposeAsync()
            {
                try
                {
                    await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
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
    }

    private static async Task<ConnectionAdmissionScenarioResult> RunReadyConnectionsAsync(
        int count,
        ServerReadyConnectionGauge gauge)
    {
        await using var server = StartServer(tls: false, authenticator: null);
        var baseline = ProcessSample.Capture();
        var gaugeBaseline = gauge.ServerConnections;

        var clients = new List<ISharpLinkClient>(count);
        var failures = 0L;
        var connectWatch = Stopwatch.StartNew();
        var pending = new List<Task>();
        for (var index = 0; index < count; index++)
        {
            pending.Add(ConnectReadyClientAsync());
            if (pending.Count >= 16)
            {
                await Task.WhenAll(pending).ConfigureAwait(false);
                pending.Clear();
            }
        }
        await Task.WhenAll(pending).ConfigureAwait(false);
        connectWatch.Stop();
        var peak = await WaitForStableSampleAsync().ConfigureAwait(false);
        await Task.Delay(200).ConfigureAwait(false);
        var observedReady = gauge.ServerConnections - gaugeBaseline;

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);
        foreach (var client in clients)
            await client.DisposeAsync().ConfigureAwait(false);
        await Task.Delay(300).ConfigureAwait(false);
        var after = ProcessSample.Capture();

        return new ConnectionAdmissionScenarioResult
        {
            Scenario = "ready-connections",
            Connections = count,
            ConnectMs = connectWatch.Elapsed.TotalMilliseconds,
            ConnectFailures = failures,
            Baseline = baseline,
            Peak = peak,
            ReadyConnectionsObserved = observedReady,
            StopMs = stopMs,
            SocketFdsReturnedToBaseline = after.FdCount <= baseline.FdCount + 2
        };

        Task ConnectReadyClientAsync()
        {
            var client = SharpClientBuilder.Create()
                .UseTcp(IPAddress.Loopback.ToString(), TcpPort)
                .Build();
            lock (clients)
                clients.Add(client);
            return AwaitAsync();

            async Task AwaitAsync()
            {
                try
                {
                    await client.ConnectAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
            }
        }
    }

    private static async Task<ConnectionAdmissionScenarioResult> RunTlsHandshakeBurstAsync(
        ServerReadyConnectionGauge gauge)
    {
        const int batchSize = 256;
        const int rounds = 3;

        await using var server = StartServer(tls: true, authenticator: null);
        var baseline = ProcessSample.Capture();
        var gaugeBaseline = gauge.ServerConnections;

        var roundWallMs = new List<double>(rounds);
        var roundCpuMs = new List<double>(rounds);
        var roundFailures = new List<long>(rounds);
        var peak = baseline;

        for (var round = 0; round < rounds; round++)
        {
            var cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
            var watch = Stopwatch.StartNew();
            var failures = await RunOneBurstAsync(batchSize).ConfigureAwait(false);
            watch.Stop();
            var cpuAfter = Process.GetCurrentProcess().TotalProcessorTime;

            roundWallMs.Add(watch.Elapsed.TotalMilliseconds);
            roundCpuMs.Add((cpuAfter - cpuBefore).TotalMilliseconds);
            roundFailures.Add(failures);
            peak = await WaitForStableSampleAsync().ConfigureAwait(false);
        }

        var stopMs = await MeasureStopAsync(server).ConfigureAwait(false);

        return new ConnectionAdmissionScenarioResult
        {
            Scenario = "tls-burst-handshake-cpu",
            Connections = batchSize,
            Rounds = rounds,
            RoundWallMs = roundWallMs,
            RoundCpuMs = roundCpuMs,
            RoundFailures = roundFailures,
            Baseline = baseline,
            Peak = peak,
            ReadyConnectionsObserved = gauge.ServerConnections - gaugeBaseline,
            StopMs = stopMs,
            SocketFdsReturnedToBaseline = true
        };

        static async Task<long> RunOneBurstAsync(int size)
        {
            var failures = 0L;
            var pending = 0;
            var tasks = new List<Task>();
            for (var index = 0; index < size; index++)
            {
                pending++;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        using var client = new TcpClient();
                        await client.ConnectAsync(IPAddress.Loopback, TcpPort).ConfigureAwait(false);
                        using var stream = new SslStream(
                            client.GetStream(),
                            leaveInnerStreamOpen: false,
                            static (_, _, _, _) => true);
                        await stream.AuthenticateAsClientAsync(
                            new SslClientAuthenticationOptions { TargetHost = "localhost" }).ConfigureAwait(false);
                    }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                    }
                    finally
                    {
                        Interlocked.Decrement(ref pending);
                    }
                }));
                if (pending >= 64)
                {
                    await Task.WhenAny(tasks).ConfigureAwait(false);
                    tasks.RemoveAll(static completed => completed.IsCompleted);
                }
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);
            return failures;
        }
    }

    // ------------------------------------------------------------------ helpers

    private static ServerHarness StartServer(bool tls, ISharpLinkServerAuthenticator? authenticator)
    {
        var builder = SharpLinkServerBuilder.Create();
        if (tls)
        {
            builder.UseTcp(
                0,
                CreateServerTlsOptions(),
                backlog: 16384,
                tlsHandshakeTimeout: SLongHandshakeTimeout);
        }
        else
        {
            builder.UseTcp(0, backlog: 16384);
        }
        builder.UseProtocol(options => options.HandshakeTimeout = SLongHandshakeTimeout);
        // Stalled connections must outlive the default 30s heartbeat timeout during evidence.
        builder.UseHeartbeat(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(20));
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
            catch (OperationCanceledException)
            {
            }
        }, runCts.Token);
        // Give the accept loop a moment to enter Running before clients arrive.
        SpinWait.SpinUntil(() => server.HealthStatus == SharpLinkHealthStatus.Ready, TimeSpan.FromSeconds(5));
        return new ServerHarness(server, runTask, runCts);
    }

    private static async Task<(List<IDisposable> Clients, long Failures, double ConnectMs)> OpenConnectionsAsync(
        int count,
        Func<int, Task<IDisposable>> opener)
    {
        var clients = new List<IDisposable>(count);
        var failures = 0L;
        var watch = Stopwatch.StartNew();
        var pending = 0;
        var tasks = new List<Task>();
        for (var index = 0; index < count; index++)
        {
            var captured = index;
            pending++;
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var connection = await opener(captured).ConfigureAwait(false);
                    lock (clients)
                        clients.Add(connection);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                }
                finally
                {
                    Interlocked.Decrement(ref pending);
                }
            }));
            if (pending >= 128)
            {
                await Task.WhenAny(tasks).ConfigureAwait(false);
                tasks.RemoveAll(static completed => completed.IsCompleted);
            }
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        watch.Stop();
        return (clients, failures, watch.Elapsed.TotalMilliseconds);
    }

    private static async Task<ProcessSample> WaitForStableSampleAsync()
    {
        var last = ProcessSample.Capture();
        var stablePolls = 0;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200).ConfigureAwait(false);
            var current = ProcessSample.Capture();
            if (current.FdCount == last.FdCount)
            {
                if (++stablePolls >= 3)
                    return current;
            }
            else
            {
                stablePolls = 0;
            }
            last = current;
        }
        return last;
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
            "CN=sharplink-evidence",
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

    private static SslServerAuthenticationOptions CreateServerTlsOptions() =>
        new() { ServerCertificate = CreateCertificate() };

    private static SslClientAuthenticationOptions CreateClientTlsOptions() =>
        new()
        {
            TargetHost = "localhost",
            RemoteCertificateValidationCallback = static (_, _, _, _) => true
        };

    // ------------------------------------------------------------------ types

    private sealed class ServerHarness : IAsyncDisposable
    {
        private bool _disposed;

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
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                await Server.StopAsync(TimeSpan.Zero).ConfigureAwait(false);
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
            try
            {
                RunCts.Dispose();
            }
            catch
            {
            }
        }
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

    private sealed class DelayedServerAuthenticator : ISharpLinkServerAuthenticator
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _entered;

        internal int Entered => Volatile.Read(ref _entered);

        public ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
            SharpLinkAuthenticationRequest request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _entered);
            return AwaitReleaseAsync(cancellationToken);
        }

        private async ValueTask<SharpLinkAuthenticationResult> AwaitReleaseAsync(CancellationToken cancellationToken)
        {
            try
            {
                await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                return SharpLinkAuthenticationResult.Success;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }

        internal void Release() => _release.TrySetResult();
    }

    /// <summary>Tracks server-side Ready connections from the sharplink.connections.active gauge.</summary>
    private sealed class ServerReadyConnectionGauge : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _serverConnections;

        internal ServerReadyConnectionGauge()
        {
            _listener.InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == "SharpLink" &&
                    instrument.Name == "sharplink.connections.active")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (!instrument.Name.Equals("sharplink.connections.active", StringComparison.Ordinal))
                    return;
                foreach (var tag in tags)
                {
                    if (tag.Key == "rpc.side" && tag.Value is "server")
                    {
                        Interlocked.Add(ref _serverConnections, value);
                        return;
                    }
                }
            });
            _listener.Start();
        }

        internal long ServerConnections => Volatile.Read(ref _serverConnections);

        public void Dispose() => _listener.Dispose();
    }
}

// ------------------------------------------------------------------ documents

public sealed class ConnectionAdmissionEvidenceDocument
{
    public string Commit { get; set; } = string.Empty;
    public string Framework { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public int ProcessorCount { get; set; }
    public string Note { get; set; } = string.Empty;
    public IReadOnlyList<ConnectionAdmissionScenarioResult> Scenarios { get; set; } = [];
}

public sealed class ConnectionAdmissionScenarioResult
{
    public string Scenario { get; set; } = string.Empty;
    public int Connections { get; set; }
    public int Rounds { get; set; }
    public double ConnectMs { get; set; }
    public long ConnectFailures { get; set; }
    public IReadOnlyList<string> ConnectFailureSamples { get; set; } = [];
    public ProcessSample Baseline { get; set; } = new();
    public ProcessSample Peak { get; set; } = new();
    public long ReadyConnectionsObserved { get; set; }
    public double StopMs { get; set; }
    public bool SocketFdsReturnedToBaseline { get; set; }
    public IReadOnlyList<double> RoundWallMs { get; set; } = [];
    public IReadOnlyList<double> RoundCpuMs { get; set; } = [];
    public IReadOnlyList<long> RoundFailures { get; set; } = [];
}

public sealed class ProcessSample
{
    public long FdCount { get; set; }
    public long ThreadCount { get; set; }
    public long WorkingSetBytes { get; set; }
    public long GcHeapBytes { get; set; }
    public long TotalAllocatedBytes { get; set; }
    public double CpuTimeMs { get; set; }

    public static ProcessSample Capture() => new()
    {
        FdCount = LinuxProcessProbe.CountSocketFds(),
        ThreadCount = LinuxProcessProbe.CountThreads(),
        WorkingSetBytes = LinuxProcessProbe.ReadVmRssBytes(),
        GcHeapBytes = GC.GetTotalMemory(forceFullCollection: false),
        TotalAllocatedBytes = GC.GetTotalAllocatedBytes(precise: true),
        CpuTimeMs = Process.GetCurrentProcess().TotalProcessorTime.TotalMilliseconds
    };
}

internal static class LinuxProcessProbe
{
    private static readonly bool SIsLinux = OperatingSystem.IsLinux();

    internal static long CountSocketFds()
    {
        if (!SIsLinux)
            return -1;
        long count = 0;
        foreach (var entry in Directory.EnumerateFileSystemEntries("/proc/self/fd"))
        {
            try
            {
                var target = new FileInfo(entry).LinkTarget;
                if (target?.StartsWith("socket:[", StringComparison.Ordinal) == true)
                    count++;
            }
            catch (IOException)
            {
            }
        }
        return count;
    }

    internal static long CountThreads()
    {
        if (!SIsLinux)
            return -1;
        return Directory.EnumerateFileSystemEntries("/proc/self/task").LongCount();
    }

    internal static long ReadVmRssBytes()
    {
        if (!SIsLinux)
            return -1;
        foreach (var line in File.ReadLines("/proc/self/status"))
        {
            if (!line.StartsWith("VmRSS:", StringComparison.Ordinal))
                continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 &&
                long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb))
            {
                return kb * 1024;
            }
        }
        return -1;
    }
}
