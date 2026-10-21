using System.Reflection;

namespace SharpLink.IntegrationTests;

public sealed partial class ColdSessionFenceResearchTests
{
    private static async Task FenceAsync(ClientConnection connection)
    {
        using var cancellation = new CancellationTokenSource(PhaseTimeout);
        // Do not invoke client.CheckHealthAsync: it can select another physical session. This
        // direct pending-table rent is a research seam, not a public API or an RPC Request.
        var operation = connection.PendingCalls.Rent(
            ResearchHealthCodec.Instance, PendingCallKind.Health, default,
            cancellation.Token, out var requestId);
        connection.Session.SendHealthCheck(requestId);
        var result = await operation.AsValueTask();
        Ensure(result.Outcome == SharpLinkHealthProbeOutcome.Success, "same-session FIFO delimiter reply");
    }

    private static Task WaitForClientDrainAsync(ClientConnection connection)
        => WaitUntilAsync(() => connection.CallAdmissionReservationCount == 0 && connection.ActiveCallCount == 0,
            "client selection reservations plus pending/untracked owners drain");

    private static Task WaitForServerDrainAsync(SharpLinkServer server)
        => WaitUntilAsync(() => IsServerDrained(server), "all server admission generations and call permits drain");

    private static bool IsServerDrained(SharpLinkServer server)
    {
        var kernel = server.AdmissionStateKernelForTests;
        if (kernel is not null)
        {
            // The registry is stable only because the external research coordinator owns
            // publication. Production needs explicit coordination of all mutation writers.
            lock (ReadField<Lock>(kernel, "_registryGate"))
            {
                if (ReadField<HashSet<AdmissionProgram>>(kernel, "_programs").Any(p => p.ActiveUses != 0))
                    return false;
            }
            if (kernel.QueuedCalls != 0 || kernel.ActivePermits != 0)
                return false;
        }
        // This ordering is important. Admission uses remain owned through transfer to call
        // permits; once every use is zero, inspect pending acquisitions and active permits.
        return server.PendingCallAdmissionsForDiagnostics == 0 && server.ActiveCallCountForDiagnostics == 0;
    }

    private static async Task AssertSameSessionUsableAsync(Harness harness)
    {
        Ensure(ReferenceEquals(harness.Connection, ReadField<ClientConnection[]>(harness.Client, "_readyConnections").Single()),
            "resume preserves the original ClientConnection and RpcSession");
        Ensure(harness.Connection.Session.IsConnected, "the original transport must remain connected");
        Ensure(await harness.Client.Get<IColdFenceResearchService>().AddAsync(20, 22).AsTask().WaitAsync(PhaseTimeout) == 42,
            "ordinary RPC remains usable after the research-only pause is released");
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string description)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            if (Stopwatch.GetElapsedTime(started) >= PhaseTimeout)
                throw new TimeoutException(description);
            await Task.Delay(5);
        }
    }

    private static T ReadField<T>(object owner, string name)
        => (T)Field(owner, name).GetValue(owner)!;
    private static void WriteField<T>(object owner, string name, T value)
        => Field(owner, name).SetValue(owner, value);
    private static FieldInfo Field(object owner, string name)
        => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing research field {owner.GetType().Name}.{name}");
    private static void Ensure(bool condition, string description)
    {
        if (!condition)
            throw new Exception(description);
    }

    private sealed class ResearchHealthCodec : IRpcCodec<SharpLinkHealthCheckResult>
    {
        internal static ResearchHealthCodec Instance { get; } = new();
        public void Serialize(in SharpLinkHealthCheckResult value, IBufferWriter<byte> buffer)
            => throw new NotSupportedException("The delimiter only decodes HealthResponse.");
        public SharpLinkHealthCheckResult Deserialize(in ReadOnlySequence<byte> buffer)
            => ProtocolV2PayloadCodec.ReadHealthResponse(buffer);
    }

    // RESEARCH ONLY. Holding a topology lock on a dedicated thread is not the proposed product
    // API. The fixture has exactly one fixed-endpoint connection and no concurrent control writer.
    // It freezes Ready publication and temporarily closes existing selection eligibility through
    // a suspended redirect. A null redirect target is deliberately test-only, not a valid public
    // refresh operation. Connection/Session stay Ready so pre-cut calls and readers can progress.
    private sealed class ResearchPause : IAsyncDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        private ResearchPause(SharpLinkClient client)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    lock (ReadField<Lock>(client, "_poolGate"))
                    {
                        var connections = ReadField<ClientConnection[]>(client, "_readyConnections");
                        Ensure(connections.Length == 1, "single fixed-endpoint research scope");
                        var previousRedirect = connections[0].SessionRefreshRedirect;
                        Ensure(previousRedirect is null, "research connection must have no refresh lineage");
                        var suspended = new SessionRefreshRedirect(connections[0]);
                        WriteField<ClientConnection?>(suspended, "_current", null);
                        WriteField(connections[0], "_sessionRefreshRedirect", suspended);
                        Thread.MemoryBarrier();
                        _held.TrySetResult();
                        try
                        {
                            Ensure(_release.Wait(TimeSpan.FromSeconds(30)), "research pause release");
                        }
                        finally
                        {
                            WriteField(connections[0], "_sessionRefreshRedirect", previousRedirect);
                            Thread.MemoryBarrier();
                        }
                    }
                    _finished.TrySetResult();
                }
                catch (Exception exception)
                {
                    _held.TrySetException(exception);
                    _finished.TrySetException(exception);
                }
            })
            { IsBackground = true, Name = "ColdFenceResearchTopologyFreeze" };
            thread.Start();
        }

        internal static async Task<ResearchPause> AcquireAsync(SharpLinkClient client)
        {
            var pause = new ResearchPause(client);
            await pause._held.Task.WaitAsync(PhaseTimeout);
            return pause;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _release.Set();
            await _finished.Task.WaitAsync(PhaseTimeout);
            _release.Dispose();
        }
    }

    // Deadline counterexamples isolate the SERVER deadline contender. A frozen monotonic
    // client clock prevents an earlier local deadline/Cancel frame from selecting a different
    // server exit path; immediate flushing keeps transport progress independent of that clock.
    private sealed class FrozenClientClock : TimeProvider
    {
        private long _timestamp = 1;
        public override long GetTimestamp() => Volatile.Read(ref _timestamp);
        internal void Expire() => Interlocked.Add(ref _timestamp, TimestampFrequency * 2);
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(SharpLinkServer server, SharpLinkClient client)
        {
            Server = server;
            Client = client;
            Connection = ReadField<ClientConnection[]>(client, "_readyConnections").Single();
        }
        internal SharpLinkServer Server { get; }
        internal SharpLinkClient Client { get; }
        internal ClientConnection Connection { get; }
        internal void ExpireClientDeadline()
            => ((FrozenClientClock)ReadField<SharpLinkRuntimeContext>(Client, "_runtimeContext").TimeProvider).Expire();

        internal static async Task<Harness> CreateAsync(bool admission, bool freezeClientClock = false)
        {
            var builder = SharpLinkServerBuilder.Create()
                .UseHeartbeat(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30));
            if (admission)
                builder.UseAdmissionControl(options =>
                {
                    options.Global.UseConcurrency(1);
                    options.QueueOneWayCalls = true;
                    options.MaxQueuedCalls = 4;
                    options.MaxQueuedBytes = 64 * 1024;
                    options.MaxQueueDelay = TimeSpan.FromSeconds(20);
                });
            builder.UseTcp(0, IPAddress.Loopback);
            var port = ((IPEndPoint)builder.Transport!.LocalEndPoint!).Port;
            var server = (SharpLinkServer)builder.Build();
            await server.StartAsync();
            var clientBuilder = SharpClientBuilder.Create().DisableRequestTimeout()
                .UseHeartbeat(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(30))
                .UseConnectionPool(options => { options.MinConnections = 1; options.MaxConnections = 1; })
                .UseTcp(IPAddress.Loopback.ToString(), port);
            if (freezeClientClock)
                clientBuilder.UseTimeProvider(new FrozenClientClock()).UseRpcSessionFlush(1, TimeSpan.FromMilliseconds(1));
            var client = (SharpLinkClient)clientBuilder.Build();
            await client.ConnectAsync();
            return new Harness(server, client);
        }

        public async ValueTask DisposeAsync()
        {
            ColdFenceResearchService.ReleaseAll();
            try { await Client.StopAsync(); }
            finally { await Server.StopAsync(TimeSpan.Zero); }
        }
    }
}
