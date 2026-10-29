using System.Buffers;
using System.Buffers.Binary;
using System.Collections;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using PinMechanism;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Server;

internal static class Program
{
    internal static readonly CancellationTokenSource Watchdog = new(TimeSpan.FromSeconds(100));
    internal const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly Process Process = Process.GetCurrentProcess();
    private static readonly List<object> Cases = [];
    private static string Stage = "arguments";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length != 6 || args[0] != "--arm" || args[1] is not ("baseline" or "candidate") ||
            args[2] != "--pair" || !int.TryParse(args[3], out var pair) || pair is < 1 or > 6 || args[4] != "--output")
        { Console.Error.WriteLine("Usage: --arm baseline|candidate --pair 1..6 --output new.json"); return 2; }
        var output = Path.GetFullPath(args[5]);
        if (File.Exists(output)) return 2;
        var metadata = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
            .ToDictionary(x => x.Key, x => x.Value);
        string status = "pass"; string? failure = null;
        object? asyncControl = null;
        try
        {
            Check(metadata["Arm"] == args[1], "arm_identity");
            Check(Environment.Version.ToString() == "10.0.14", "private_runtime_required");
            // Fixed rotation is identical within a pair, independent of observed results.
            var cells = new (string Route, int N)[] {
                ("static", 0), ("static", 1), ("static", 100),
                ("module-configured", 0), ("module-configured", 1), ("module-configured", 100),
                ("legacy", 0), ("legacy", 100) };
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = cells[(i + pair - 1) % cells.Length];
                await Measure(cell.Route, cell.N);
            }
            Stage = "async-allocation-control";
            await using var control = new Fixture("static", 1, forceAsync: true);
            var start = GC.GetTotalAllocatedBytes(precise: true);
            await control.Batch(32);
            await control.Drain();
            var allocated = GC.GetTotalAllocatedBytes(precise: true) - start;
            control.AssertSettled(); control.ValidateCounts(32);
            await control.Close();
            asyncControl = new { passed = true, ops = 32, itemsPerInvocation = 1,
                producerGets = control.Service.Gets, producerMoves = control.Service.Moves,
                producerDisposes = control.Service.Disposes, allocatedBytes = allocated,
                scope = "one_cold_async_producer_control_allocation_only_not_timing_or_benefit",
                callMapEmpty = true, callStatesReleased = true, finalQueuedBytes = 0 };
        }
        catch (Exception e)
        {
            status = "fail";
            failure = e is TimeoutException ? "watchdog" : e is OperationCanceledException ? "cancelled" :
                e is InvalidOperationException ? "invariant" : "exception";
            Console.Error.WriteLine($"FAIL pin stage={Stage} class={failure} type={e.GetType().Name}");
            // Full details stay in this runner-local log, never the bounded public summary.
            Console.Error.WriteLine(e);
        }
        var frameworks = new[] { Canonical(typeof(object).Assembly.Location) };
        var binaryHashes = Directory.GetFiles(AppContext.BaseDirectory, "*.dll")
            .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(p => Path.GetFileName(p), Hash);
        _ = JsonSerializer.Serialize(new { status, cases = Cases, asyncControl });
        // Assembly enumeration can itself load framework helpers; reach closure before reporting.
        var inventory = new Dictionary<string, AssemblyIdentity>();
        for (var iteration = 0; iteration < 8; iteration++)
        {
            var before = inventory.Count;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
            {
                var name = assembly.GetName().Name!;
                var location = Canonical(assembly.Location);
                var identity = new AssemblyIdentity(name, location, assembly.ManifestModule.ModuleVersionId, Hash(location));
                Check(!inventory.TryGetValue(name, out var old) || old == identity, "duplicate_loaded_identity");
                inventory[name] = identity;
            }
            if (inventory.Count == before) break;
            Check(iteration < 7, "assembly_closure_unstable");
        }
        var loaded = inventory.Values.OrderBy(x => x.name, StringComparer.Ordinal).ToArray();
        var result = new { schemaVersion = 1, status, failure, stage = Stage, arm = args[1], pair,
            reviewedSourceCommit = metadata["ExpectedProductCommit"], sourceTree = metadata["SourceTree"],
            runtimeVersion = Environment.Version.ToString(), runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            processorCount = Environment.ProcessorCount, stopwatchFrequency = Stopwatch.Frequency,
            cpuTicksPerSecond = TimeSpan.TicksPerSecond,
            scope = "server_call_pin_mechanism_not_TCP_QPS_p99",
            boundary = "per_invocation_state_rent_generated_guard_pump_release_retire_and_final_drain",
            dynamicScope = "module_token_registration_only_not_DynamicChannel_or_module_lease_dispatch",
            transport = "in_memory_validating_sink_with_deterministic_per_item_credit_return",
            measuredProducer = "synchronous-finite", cases = Cases, asyncControl,
            loadedAssemblies = loaded, loadedAssemblyCount = loaded.Length, binaryHashes, frameworkLocations = frameworks };
        var json = JsonSerializer.SerializeToUtf8Bytes(result, new JsonSerializerOptions { WriteIndented = true });
        Check(json.Length <= 131072, "bounded_result");
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write)) file.Write(json);
        Console.WriteLine($"{status.ToUpperInvariant()} pin pair={pair} cells={Cases.Count}");
        return status == "pass" ? 0 : 1;
    }

    private static async Task Measure(string route, int items)
    {
        Stage = route + "-n" + items;
        await using var fixture = new Fixture(route, items, forceAsync: false);
        await Phase(fixture, 1);
        fixture.AssertSettled();
        var before = fixture.Snapshot();
        var start = Sample.Take();
        var ops = await Phase(fixture, 2);
        var costs = start.Finish();
        var actual = fixture.Snapshot() - before;
        fixture.AssertSettled();
        Validate(actual, ops, items);
        var flow = fixture.FlowSnapshot();
        await fixture.Close();
        Cases.Add(new { @case = Stage, route = route == "static" ? "tracked-static-generated-stub" :
                route == "module-configured" ? "module-configured-generated-stub-not-dynamic-channel" : "legacy-untracked-generated-stub",
            itemsPerInvocation = items, ops, allocatedBytes = costs.Allocated, cpuTicks = costs.Cpu, elapsedTicks = costs.Elapsed,
            warmupSeconds = 1, measureSeconds = 2, producer = "synchronous-finite",
            dataFrames = actual.Data, completeFrames = actual.Complete, payloadChecksum = actual.Checksum,
            producerGets = actual.Gets, producerMoves = actual.Moves, producerDisposes = actual.Disposes,
            callMapEmpty = true, callStatesReleased = true, finalQueuedBytes = 0, sendStates = 0, sendWaiters = 0,
            sendCredit = flow.Credit, connectionWindow = flow.Window, passed = true });
    }

    private static async Task<long> Phase(Fixture fixture, int seconds)
    {
        var until = Stopwatch.GetTimestamp() + seconds * Stopwatch.Frequency;
        long ops = 0;
        do { Watchdog.Token.ThrowIfCancellationRequested(); await fixture.Batch(32); await fixture.Drain(); ops += 32; }
        while (Stopwatch.GetTimestamp() < until);
        return ops;
    }

    internal static void Validate(Census c, long ops, int n)
    {
        Check(c.Data == ops * n && c.Complete == ops && c.Checksum == ops * n * (n - 1) / 2,
            "frame_item_checksum_census");
        Check(c.Gets == ops && c.Moves == ops * (n + 1) && c.Disposes == ops, "producer_boundary_census");
    }
    internal static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    internal static object Field(object o, string name) => (o.GetType().GetField(name, Instance)
        ?? throw new InvalidOperationException("missing_field_" + name)).GetValue(o)!;
    private static string Hash(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))).ToLowerInvariant();
    private static string Canonical(string p)
    {
        Check(Path.IsPathFullyQualified(p) && File.Exists(p), "assembly_file");
        var path = Path.GetFullPath(p);
        for (string? c = path; c is not null; c = Path.GetDirectoryName(c))
            Check((File.GetAttributes(c) & FileAttributes.ReparsePoint) == 0, "assembly_symlink");
        return path;
    }
    private sealed record AssemblyIdentity(string name, string location, Guid mvid, string sha256);
    private readonly record struct Sample(long Allocated, long Elapsed, long Cpu)
    {
        internal static Sample Take() { Process.Refresh(); return new(GC.GetTotalAllocatedBytes(true), Stopwatch.GetTimestamp(), Process.TotalProcessorTime.Ticks); }
        internal Sample Finish() { var e = Take(); return new(e.Allocated - Allocated, e.Elapsed - Elapsed, e.Cpu - Cpu); }
    }
}

internal readonly record struct Census(long Data, long Complete, long Checksum, long Gets, long Moves, long Disposes)
{
    public static Census operator -(Census a, Census b) => new(a.Data - b.Data, a.Complete - b.Complete,
        a.Checksum - b.Checksum, a.Gets - b.Gets, a.Moves - b.Moves, a.Disposes - b.Disposes);
}

internal sealed class Fixture : IAsyncDisposable
{
    private readonly SharpLinkServer _server;
    private readonly RpcSession _session;
    private readonly ServerGeneratedBridge _bridge;
    private readonly StripedLongMap<ServerCallCancellationState> _calls = new();
    private readonly Pipe _input = new();
    private readonly ValidatingSink _sink;
    private readonly CancellationTokenSource _connection = new(), _stop = new(), _module = new();
    private readonly IRpcStub _stub;
    private readonly RpcMethodDescriptor _method;
    private readonly long _methodId;
    private readonly string _route;
    private long _requestId;
    private Task? _close;
    internal readonly PinStreamService Service;

    internal Fixture(string route, int items, bool forceAsync)
    {
        _route = route;
        var assembly = typeof(PinStreamService).Assembly;
        RuntimeHelpers.RunModuleConstructor(assembly.ManifestModule.ModuleHandle);
        var attribute = assembly.GetCustomAttribute<SharpLinkGeneratedAssemblyManifestAttribute>()!;
        Program.Check(attribute is not null && attribute.ApiVersion == 5 && attribute.ProtocolVersion == 2 &&
            attribute.GeneratorVersion == "3.0.0" && attribute.AbiIdentity == "sharplink-3.0-api5-owner-bound-unsafe-blit-v1", "generated_abi");
        var manifest = (ISharpLinkGeneratedAssemblyManifest)Activator.CreateInstance(attribute!.ManifestType)!;
        _server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(new FixedGeneratedManifestSource([manifest]))
            .DisableAutomaticServiceRegistration().UseTransport(new IdleListener()).Build();
        var runtime = (SharpLinkRuntimeContext)Program.Field(_server, "_runtimeContext");
        _sink = new ValidatingSink(runtime.Protocol, items);
        _session = new RpcSession(new Connection(_input.Reader, _sink), new RpcSessionCreationOptions(RpcSessionRole.Server, runtime));
        Program.Check(_session.TryCompleteHandshake(new NegotiatedSessionOptions(ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.FlowControl, runtime.Protocol.MaxFramePayloadBytes,
            runtime.FlowControl.StreamReceiveWindowBytes, runtime.FlowControl.ConnectionReceiveWindowBytes)), "handshake");
        _sink.Session = _session;
        _calls.EnableCountTracking();
        _bridge = new ServerGeneratedBridge(_server, _session, _calls);
        var contract = manifest.Contracts.Single(x => x.ContractType == typeof(IPinStream));
        _methodId = contract.Methods.Single().MethodId;
        _stub = contract.StubFactory(runtime.Codecs);
        Program.Check(_stub.TryGetMethodDescriptor(_methodId, out _method), "generated_method");
        Service = new PinStreamService { Items = items, ForceAsync = forceAsync };
    }

    internal async Task Batch(int count)
    {
        for (var i = 0; i < count; i++)
        {
            var id = ++_requestId;
            ServerCallCancellationState? state = null;
            if (_route != "legacy")
            {
                state = ServerCallCancellationState.Rent(id, default, TimeProvider.System, _connection.Token,
                    _stop.Token, _route == "module-configured" ? _module.Token : CancellationToken.None,
                    supportsCooperativeCancellation: true);
                _calls.Set(id, state);
            }
            try
            {
                var token = state?.InvocationToken ?? CancellationToken.None;
                using var scope = SharpLinkCallContext.Push(new SharpLinkServerInvocationContext(_method, id,
                    _session.Id, null, null, null, default, TimeProvider.System, null, token));
                await _stub.InvokeNoReturnCancellableAsync(Service, _bridge, _methodId, id,
                    ReadOnlySequence<byte>.Empty, token).ConfigureAwait(false);
            }
            finally
            {
                if (state is not null)
                {
                    Program.Check(state.TryClaimResponse(), "unexpected_call_terminal");
                    Program.Check(_calls.TryRemove(id, state), "call_unlink");
                    state.Dispose();
                    // ReturnCore resets RequestId only after the last lease releases. This direct
                    // per-call assertion catches retained states without hot reflection/counter hooks.
                    Program.Check(state.RequestId == 0, "call_state_not_released");
                }
            }
        }
    }

    internal async Task Drain()
    {
        while (_session.QueuedSendBytes != 0)
        { Program.Watchdog.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        await _session.FlushSendQueueAsync(Program.Watchdog.Token);
    }
    internal Census Snapshot() => new(_sink.DataFrames, _sink.CompleteFrames, _sink.Checksum,
        Service.Gets, Service.Moves, Service.Disposes);
    internal void ValidateCounts(long ops) => Program.Validate(Snapshot(), ops, Service.Items);
    internal (long Credit, int Window) FlowSnapshot()
    {
        var protocol = Program.Field(_session, "_protocolState");
        var controller = (StreamFlowController)protocol.GetType().GetProperty("FlowController", Program.Instance)!.GetValue(protocol)!;
        lock ((Lock)Program.Field(controller, "_gate"))
        {
            Program.Check(controller.ActiveSendStreamCount == 0 && controller.RetainedSendStreamCount == 0 &&
                ((ICollection)Program.Field(controller, "_waiters")).Count == 0 &&
                (int)Program.Field(controller, "_pendingSendStateWaiterCount") == 0, "send_state_retention");
            var window = (int)Program.Field(controller, "_connectionWindow");
            Program.Check(controller.SendConnectionCredit == window, "send_credit_not_restored");
            return (controller.SendConnectionCredit, window);
        }
    }
    internal void AssertSettled()
    {
        Program.Check(_calls.Count == 0 && _session.QueuedSendBytes == 0 && _sink.PendingBytes == 0 &&
            _sink.PendingItems == 0, "not_settled");
        _ = FlowSnapshot();
    }
    internal Task Close() => _close ??= CloseCore().WaitAsync(TimeSpan.FromSeconds(10));
    private async Task CloseCore()
    {
        await Drain(); AssertSettled();
        await _session.DisposeAsync(); await _input.Writer.CompleteAsync(); await _server.DisposeAsync();
        _module.Dispose(); _connection.Dispose(); _stop.Dispose();
        Program.Check(_calls.Count == 0 && _session.QueuedSendBytes == 0, "post_disposal_owners");
    }
    public async ValueTask DisposeAsync() => await Close();
    private sealed class IdleListener : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Connection(PipeReader input, PipeWriter output) : ITransportConnection
    {
        public string Id => "pin-mechanism";
        public PipeReader Input => input; public PipeWriter Output => output;
        public EndPoint? LocalEndPoint => null; public EndPoint? RemoteEndPoint => null;
        public async ValueTask DisposeAsync() { await Output.CompleteAsync(); await Input.CompleteAsync(); }
    }
}

// Real generated frames, validated at transport flush. No network or background client.
// Credit returns are deterministic per payload, not natural WindowUpdate counts/performance.
internal sealed class ValidatingSink(SharpLinkProtocolOptions protocol, int items) : PipeWriter
{
    private readonly byte[] _buffer = new byte[262144];
    private int _written, _item;
    private long _request = 1;
    internal RpcSession Session = null!;
    internal long DataFrames, CompleteFrames, Checksum;
    internal int PendingBytes => _written;
    internal int PendingItems => _item;
    public override void Advance(int bytes) { Program.Check(bytes >= 0 && bytes <= _buffer.Length - _written, "sink_capacity"); _written += bytes; }
    public override Memory<byte> GetMemory(int sizeHint = 0) { Capacity(sizeHint); return _buffer.AsMemory(_written); }
    public override Span<byte> GetSpan(int sizeHint = 0) { Capacity(sizeHint); return _buffer.AsSpan(_written); }
    private void Capacity(int n) => Program.Check(n >= 0 && n <= _buffer.Length - _written, "sink_capacity");
    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = new ReadOnlySequence<byte>(_buffer.AsMemory(0, _written));
        while (ProtocolV2FrameParser.TryReadFrame(ref remaining, protocol, out var header, out var payload))
        {
            Program.Check(header.RequestId == (ulong)_request && header.Flags == ProtocolV2FrameFlags.None,
                "wire_request_or_error");
            Program.Check(payload.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(payload.FirstSpan) == 0, "stream_id");
            if (header.Type == ProtocolV2FrameType.StreamData)
            {
                Program.Check(payload.Length == 6 && _item < items && BinaryPrimitives.ReadInt32LittleEndian(payload.FirstSpan[2..]) == _item,
                    "wire_item");
                Checksum += _item++; DataFrames++;
                Session.ApplyWindowUpdate(_request, new ProtocolV2WindowUpdate(0, 4));
            }
            else
            {
                Program.Check(header.Type == ProtocolV2FrameType.StreamComplete && payload.Length == 2 && _item == items,
                    "wire_complete");
                _request++; _item = 0; CompleteFrames++;
            }
        }
        var left = checked((int)remaining.Length);
        if (left != 0) remaining.CopyTo(_buffer);
        _written = left;
        return ValueTask.FromResult(new FlushResult(false, false));
    }
    public override void CancelPendingFlush() { }
    public override void Complete(Exception? exception = null) { }
}
