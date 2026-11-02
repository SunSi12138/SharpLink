using System.Buffers;
using System.Net;
using System.Reflection;
using SharpLink.RollbackPlugin;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

[NotInParallel("rollback-plugin")]
public class ContractManifestReadyBoundaryTests
{
    [Test]
    public async Task BootstrapManifestWriteMustObserveReadyRegistryBoundary(CancellationToken testCancellationToken)
    {
        var owner = new ReadyFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize();
            var connection = owner.Connection!;
            var listener = owner.Listener!;
            owner.Endpoint = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .UseTransport(listener)
                .Build();
            var server = owner.Server = (SharpLinkServer)owner.Endpoint;
            var registry = (ServerConnectionRegistry)typeof(SharpLinkServer)
                .GetField("_connectionRegistry", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(server)!;
            var mutationObserved = owner.MutationObserved;
            var stateAtBootstrapWrite = ServerConnectionLifecycleState.Handshaking;
            SharpLinkAssemblyRegistrationResult registrationResult = default;

            connection.RunOnNextOutputBufferRequest(() =>
            {
                // The first packet is HandshakeResponse. Arm the next packet write, which must be
                // the single bootstrap ContractManifest for a manifest-capable connection.
                connection.RunOnNextOutputBufferRequest(() =>
                {
                    try
                    {
                        var active = registry.SnapshotActive();
                        if (active.Length != 1)
                            throw new InvalidOperationException($"expected one active connection, found {active.Length}");
                        var serverConnection = active[0];
                        stateAtBootstrapWrite = serverConnection.LifecycleState;
                        registrationResult = server.RegisterAssembly(typeof(RollbackMarker).Assembly);
                        mutationObserved.TrySetResult(true);
                    }
                    catch (Exception exception)
                    {
                        mutationObserved.TrySetException(exception);
                    }
                });
            });

            owner.Run = owner.Keep(server.RunUntilStoppedAsync().AsTask());
            await owner.Keep(WaitForConnectionAsync(registry));

            var requestPayload = owner.Payload = new PooledByteBufferWriter();
            var request = new ProtocolV2HandshakeRequest(
                ProtocolV2Constants.MinorVersion,
                ProtocolV2Negotiator.MandatoryCapabilities,
                ProtocolV2Negotiator.MandatoryCapabilities,
                4 * 1024 * 1024,
                1024 * 1024,
                16 * 1024 * 1024,
                ReadOnlyMemory<byte>.Empty,
                BindingLimits: Protocol3BindingLimits.Default);
            ProtocolV2PayloadCodec.WriteHandshakeRequest(
                requestPayload,
                request,
                new SharpLinkProtocolOptions());
            await owner.Keep(connection.InjectFrameAsync(
                ProtocolV2FrameType.HandshakeRequest,
                ProtocolV2FrameFlags.None,
                0,
                requestPayload.WrittenMemory));

            await owner.Wait(owner.Keep(connection.WaitForSentFrame(ProtocolV2FrameType.HandshakeResponse)));
            var bootstrapFrame = await owner.Wait(owner.Keep(connection.WaitForSentFrame(ProtocolV2FrameType.ContractManifest)));
            await owner.Wait(mutationObserved.Task);

            Ensure(stateAtBootstrapWrite == ServerConnectionLifecycleState.Ready,
                "bootstrap manifest must not become writable while the server connection is still Handshaking");
            Ensure(registrationResult.Succeeded,
                "the deterministic bootstrap-window mutation must publish successfully");

            var bootstrap = ProtocolV2ContractManifestCodec.Read(
                new ReadOnlySequence<byte>(bootstrapFrame.Payload),
                new SharpLinkProtocolOptions());
            var refreshFrame = await owner.Wait(owner.Keep(connection.WaitForSentFrame(ProtocolV2FrameType.ContractManifest)));
            var refresh = ProtocolV2ContractManifestCodec.Read(
                new ReadOnlySequence<byte>(refreshFrame.Payload),
                new SharpLinkProtocolOptions());

            Ensure(refresh.Generation > bootstrap.Generation,
                "a registry mutation after the Ready/bootstrap boundary must be published as a later generation");
        }
        catch (Exception failure) { owner.Failures.Add(failure); }
        finally { await owner.FinishAsync(); }
        owner.ThrowIfFailed();
    }

    private static async Task WaitForConnectionAsync(ServerConnectionRegistry registry)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (registry.Count == 0)
        {
            if (DateTime.UtcNow >= deadline)
                throw new TimeoutException("server did not publish the accepted connection");
            await Task.Delay(1);
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    // Concrete ownership for this one Ready/manifest test, not a shared task framework.
    // The existing codec switch excludes intentional adapter disposal faults from this
    // positive publication-order control; other rollback controls still exercise them.
    private sealed class ReadyFixture
    {
        private static readonly List<ReadyFixture> Retained = [];
        private readonly object _gate = new();
        private readonly List<(Task Source, Task<Exception?> Outcome)> _sources = [];
        private string? _previousCodec;
        private bool _codecSnapshot;
        private bool _codecMutated;
        internal readonly List<Exception> Failures = [];
        internal readonly TaskCompletionSource<bool> MutationObserved =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Task<Exception?> _mutationOutcome;
        internal ReadyFixture() => _mutationOutcome = ReadOutcome(MutationObserved.Task);
        internal TestTransportConnection? Connection;
        internal OwnedConnection? Wire;
        internal SingleConnectionListener? Listener;
        internal ISharpLinkServer? Endpoint;
        internal SharpLinkServer? Server;
        internal Task? Run;
        internal PooledByteBufferWriter? Payload;

        internal void Initialize()
        {
            _previousCodec = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC");
            _codecSnapshot = true;
            _codecMutated = true; // A throwing setter still requires restoration of the known snapshot.
            Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
            Connection = new TestTransportConnection();
            Wire = new OwnedConnection(this, Connection);
            Listener = new SingleConnectionListener(Wire);
        }

        internal Task Keep(Task source)
        {
            lock (_gate) _sources.Add((source, ReadOutcome(source)));
            return source;
        }

        internal Task<T> Keep<T>(Task<T> source)
        {
            _ = Keep((Task)source);
            return source;
        }

        private static async Task<Exception?> ReadOutcome(Task source)
        {
            try { await source; return null; }
            catch (Exception failure)
            {
                // Preserve every Task sibling, or the actual cancellation receipt.
                return source.Exception is { } aggregate ? aggregate : failure;
            }
        }

        internal async Task<T> Wait<T>(Task<T> source)
        {
            // Preserve the actual waiter task too; a timeout cannot be erased by a
            // source which happens to finish between the catch and final sweep.
            var waiter = source.WaitAsync(TimeSpan.FromSeconds(2));
            _ = Keep(waiter);
            return await waiter;
        }

        private async Task Join(Task source)
        {
            try { await source.WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (Exception failure) { Failures.Add(failure); }
        }

        private Task? StartCleanup(Func<ValueTask> action)
        {
            try { return Keep(action().AsTask()); }
            catch (Exception failure) { Failures.Add(failure); return null; }
        }

        internal async Task FinishAsync()
        {
            // Do not pass the already-cancelled test token to a physical Stop.
            // Each original cleanup source is invoked once and independently joined.
            Task? stop = null;
            Task? dispose = null;
            if (Endpoint is { } server)
            {
                stop = StartCleanup(() => server.StopAsync(TimeSpan.Zero));
                if (stop is not null) await Join(stop);
                if (Run is { } run) await Join(run);
                dispose = StartCleanup(server.DisposeAsync);
                if (dispose is not null) await Join(dispose);
            }

            // A failed logical Stop can leave real physical owners retained. Read
            // existing lifecycle sources; do not substitute Stop completion for them.
            if (Server is { } ownedServer)
            {
                var lifecycle = ownedServer.LifecycleForDiagnostics;
                var physicalSources = new[]
                {
                    lifecycle.StrictPhysicalDrainForDiagnostics.Drain,
                    lifecycle.ShutdownCleanupObserverTaskForDiagnostics,
                    lifecycle.DeferredServiceCleanupTaskForDiagnostics,
                    lifecycle.TerminalFailureObserverTaskForDiagnostics,
                    lifecycle.StrictShutdownCleanupObserverForDiagnostics
                };
                foreach (var source in physicalSources)
                    if (source is not null) { _ = Keep(source); await Join(source); }
            }

            // Seal future accepts before deciding who owns the connection. An
            // accepted connection remains server-owned, even when Stop times out.
            Task? bareDispose = null;
            if (Listener is { } listener)
            {
                var listenerDispose = StartCleanup(listener.DisposeAsync);
                if (listenerDispose is not null) await Join(listenerDispose);
            }
            else if (Wire is { } unoffered)
            {
                var unofferedDispose = StartCleanup(unoffered.DisposeAsync);
                if (unofferedDispose is not null) await Join(unofferedDispose);
            }
            else if (Connection is { } bare)
            {
                bareDispose = StartCleanup(bare.DisposeAsync);
                if (bareDispose is not null) await Join(bareDispose);
            }

            // A completed server owner must have actually disposed the accepted
            // connection. Do not manufacture that missing cleanup on its behalf.
            var wireDispose = Wire?.Disposal;
            if (wireDispose is not null) await Join(wireDispose);
            var connectionEnded = Connection is null || (Wire is not null
                ? wireDispose is { IsCompletedSuccessfully: true }
                : bareDispose is { IsCompletedSuccessfully: true });
            var ownersEnded = Endpoint is null ||
                (stop is { IsCompletedSuccessfully: true } && dispose is { IsCompletedSuccessfully: true } &&
                 (Run is null || Run.IsCompletedSuccessfully) &&
                 Endpoint.LifecycleState == SharpLinkServerLifecycleState.Stopped);
            if (Listener is { Accepted: true } && wireDispose is null)
            {
                Failures.Add(new InvalidOperationException("accepted connection has no actual disposal source"));
                ownersEnded = false;
            }

            // Stop/run publication is latched BEFORE the snapshot. If they have
            // not ended, later connection-disposal publication remains retained.
            (Task Source, Task<Exception?> Outcome)[] sources;
            lock (_gate) sources = _sources.ToArray();
            foreach (var pair in sources) await Join(pair.Source);
            // A terminal but failed transport Dispose is not proof that its
            // physical readers/writers ended, including before Build returned.
            var allEnded = ownersEnded && connectionEnded;
            foreach (var pair in sources)
                allEnded &= pair.Source.IsCompleted;
            // This irreversible latch prevents late completion from turning a
            // timeout/inconclusive ownership sample into permission to release.
            foreach (var pair in sources)
            {
                if (!pair.Source.IsCompleted) continue;
                await Join(pair.Outcome);
                if (!pair.Outcome.IsCompletedSuccessfully) { allEnded = false; continue; }
                var failure = await pair.Outcome;
                if (failure is not null) Failures.Add(failure);
            }
            if (!allEnded)
            {
                Failures.Add(new InvalidOperationException("Ready fixture retains unfinished physical owners and isolation"));
                lock (Retained) Retained.Add(this);
                return;
            }
            // The passive callback receipt is not an active worker. Once server
            // owners have ended it cannot be newly invoked; missing completion
            // is an error, never a fabricated successful callback.
            if (Run is not null && !MutationObserved.Task.IsCompleted)
                Failures.Add(new InvalidOperationException("bootstrap callback never completed"));
            else if (MutationObserved.Task.IsCompleted)
            {
                await Join(_mutationOutcome);
                if (!_mutationOutcome.IsCompletedSuccessfully)
                {
                    Failures.Add(new InvalidOperationException("bootstrap callback outcome observation remains pending"));
                    lock (Retained) Retained.Add(this);
                    return;
                }
                var outcome = await _mutationOutcome;
                if (outcome is not null) Failures.Add(outcome);
            }
            try { Payload?.Dispose(); }
            catch (Exception failure) { Failures.Add(failure); }
            var restored = true;
            if (_codecSnapshot && _codecMutated)
            {
                try { Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", _previousCodec); }
                catch (Exception failure) { Failures.Add(failure); restored = false; }
            }
            if (!restored)
            {
                lock (Retained) Retained.Add(this);
                return;
            }
            try { RollbackState.TestIsolation.Release(); }
            catch (Exception failure) { Failures.Add(failure); }
        }

        internal void ThrowIfFailed()
        {
            if (Failures.Count != 0) throw new AggregateException(Failures);
        }
    }

    private sealed class OwnedConnection(ReadyFixture owner, TestTransportConnection inner) : ITransportConnection
    {
        private readonly object _gate = new();
        private Task? _disposal;
        private Exception? _invocationFailure;
        internal Task? Disposal { get { lock (_gate) return _disposal; } }
        public string Id => inner.Id;
        public System.IO.Pipelines.PipeReader Input => inner.Input;
        public System.IO.Pipelines.PipeWriter Output => inner.Output;
        public EndPoint? LocalEndPoint => inner.LocalEndPoint;
        public EndPoint? RemoteEndPoint => inner.RemoteEndPoint;
        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                // A synchronous invocation failure leaves no claimed source.
                // No wrapper success may substitute for the actual Dispose task.
                if (_invocationFailure is { } previous)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(previous).Throw();
                try { _disposal ??= owner.Keep(inner.DisposeAsync().AsTask()); }
                catch (Exception failure) { _invocationFailure = failure; throw; }
                return new ValueTask(_disposal);
            }
        }
    }

    private sealed class SingleConnectionListener(OwnedConnection connection) : IServerTransportListener
    {
        private readonly object _gate = new();
        private bool _accepted;
        private bool _sealed;
        internal bool Accepted { get { lock (_gate) return _accepted; } }
        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (!_sealed && !_accepted)
                {
                    _accepted = true;
                    return connection;
                }
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            lock (_gate)
            {
                _sealed = true;
                return _accepted ? ValueTask.CompletedTask : connection.DisposeAsync();
            }
        }
    }
}
