using System.Diagnostics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Client;

public sealed partial class ContractScopedRoutePhysicalFailureControls
{
    private sealed partial class PhysicalFixture
    {
        private static readonly List<PhysicalFixture> Quarantine = [];
        private readonly Lock _ownersGate = new();
        private readonly List<Owner> _owners = [];
        private readonly ManualTimeProvider _clock = new();
        private readonly GatedFactory[] _factories;
        private readonly TestSentFrame?[] _binds = new TestSentFrame?[2];
        private readonly bool[] _replyStarted = new bool[2];
        private SharpLinkClient? _client;
        private SharpLinkClient.ContractCallSite? _site;
        private ClientConnection? _connection;
        private Owner? _stop;
        private bool _reservation;
        private Exception? _acceptedCleanupEnvelope;
        internal SharpLinkClient Client => _client ?? throw new InvalidOperationException("Client not initialized.");
        internal SharpLinkClient.ContractCallSite Site => _site ?? throw new InvalidOperationException("Site not initialized.");
        internal ClientConnection Connection => _connection ?? throw new InvalidOperationException("Physical owner not captured.");
        internal GatedTransport Peer => _factories[0].Transport;
        internal SharpLinkClient.ContractRouteTopology Topology
            => Read<SharpLinkClient.ContractRouteTopology>(Client, "_contractRouteTopology");

        internal PhysicalFixture(bool holdSecond = false)
        {
            _factories = [new(this, "A", held: true), new(this, "B", held: holdSecond)];
        }

        internal async Task InitializeAsync(bool dynamicTopology = false)
        {
            StaticEndpointConfiguration[] endpoints =
            [
                new(new() { Id = "failure-a", Address = new SharpLinkTcpAddress("127.0.0.1", 19101) }, _factories[0]),
                new(new() { Id = "failure-b", Address = new SharpLinkTcpAddress("127.0.0.1", 19102) }, _factories[1])
            ];
            void Configure(SharpClientBuilder builder)
            {
                builder.UseTimeProvider(_clock);
                builder.UseLoadBalancing(SharpLinkLoadBalancingStrategy.RoundRobin);
                builder.UseCluster(options =>
                {
                    options.MinReadyEndpoints = 2;
                    options.MaxEndpoints = 2;
                    options.MaxConnections = 2;
                    options.MaxConnectionsPerEndpoint = 1;
                });
            }
            _client = dynamicTopology
                ? ClientBuilderTestHelper.BuildDynamic(new FixedResolver(new(1, endpoints.Select(item => item.Endpoint).ToArray())),
                    endpoint => endpoint.Id == "failure-a" ? _factories[0] : _factories[1], Configure)
                : ClientBuilderTestHelper.BuildStatic(endpoints, Configure);
            await ObserveAsync(Own("original Connect", Client.ConnectAsync().AsTask()));
            await ObserveAsync(Own("original readiness", Client.WaitForReadinessAsync(2).AsTask()));
            Ensure(_factories.All(factory => factory.ConnectCount == 1), "exactly one real physical session per endpoint");
            _connection = Topology.Endpoints.Single(endpoint => endpoint.Endpoint.Id == "failure-a").Connections.Single();
            _site = (SharpLinkClient.ContractCallSite)ClientInvokerTestHelper.CreateChannel(Client);
            var prepare = Own("original Prepare", Site.PrepareAsync(Bound).AsTask());
            for (var index = 0; index < 2; index++)
            {
                var reader = Own($"original Bind reader {index}",
                    _factories[index].Peer.Connection.WaitForSentFrame(ProtocolV2FrameType.BindContract));
                await ObserveAsync(reader);
                _binds[index] = ((Task<TestSentFrame>)reader.Source).Result;
                var request = Protocol3BindingControlCodec.ReadBindContract(new(_binds[index]!.Value.Payload));
                Ensure(request.ContractId == ManualClientContractFixture.ContractId &&
                       request.Hash == ManualClientContractFixture.AssemblyHash &&
                       request.Requirement == Protocol3BindingRequirement.Compatible && request.Receipt is null,
                    "both peers bind the real local contract identity");
            }
            await UntilAsync(() => CaptureFramework("ContractBinding", "original Bind") == 2,
                "both actual pending Bind tasks must be captured before either success reply");
            for (var index = 0; index < 2; index++) await ConfirmAsync(index);
            await ObserveAsync(prepare);
            foreach (var binding in Snapshot().Where(owner => owner.Stage == "original Bind")) await ObserveAsync(binding);
            Ensure(Site.CaptureContractRouteForDiagnostics().Targets.Length == 2 &&
                   Connection.Binding.ActiveOperationCount == 0,
                "both exact confirmed targets are published with no pending control owner before the cold fault");
        }

        private async Task ConfirmAsync(int index)
        {
            var frame = _binds[index] ?? throw new InvalidOperationException("Missing original Bind frame.");
            Ensure(!_replyStarted[index], "a reply whose outcome is unknown must never be resent");
            var peer = _factories[index].Peer.Connection;
            var authority = peer.Authority;
            using var payload = new PooledByteBufferWriter();
            Protocol3BindingControlCodec.WriteBindResult(payload, new Protocol3BindResult(
                Protocol3BindStatus.Success, Protocol3BindingToken.Create(0, 1, authority.Limits.Slots),
                ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash,
                1, 1, authority.ServerInstanceId, authority.RegistryEpoch), authority.Limits.Slots);
            var bytes = payload.WrittenMemory.ToArray();
            _replyStarted[index] = true;
            await ObserveAsync(Own($"original BindResult injection {index}", peer.InjectFrameAsync(
                ProtocolV2FrameType.BindResult, ProtocolV2FrameFlags.None, frame.Header.RequestId, bytes)));
        }

        internal void FailSiteBuild(Exception marker)
            => Client.ContractRouteBuildForTesting = (site, stage) =>
            {
                if (ReferenceEquals(site, Site) && stage == "buckets") throw marker;
            };
        internal void ClearHooks()
        {
            if (_client is null) return;
            Client.ContractRouteBuildForTesting = null;
            Client.ContractRoutePublicationForTesting = null;
            Client._afterSessionRefreshEligibilitySwapTestHook = null;
        }

        internal void FaultActualSession()
        {
            // Invoke the actual one-argument session fault entry, not cluster.HandleDisconnected
            // or a made-up callback/task. Its real event invocation has a swallowing catch.
            var fault = typeof(RpcSession).GetMethod("Fault", BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, types: [typeof(Exception)], modifiers: null)
                ?? throw new InvalidOperationException("Actual RpcSession.Fault entry missing.");
            try { fault.Invoke(Connection.Session, [new IOException("external physical fault")]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            {
                ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            }
            Ensure(Connection.HasObservedFatalFailureForAdmission, "real session disconnect callback performed fatal admission cut");
        }

        internal bool ContainsPhysicalOwner()
        {
            var cluster = Read<object>(Client, "_cluster");
            var gate = Read<Lock>(cluster, "_gate");
            lock (gate)
            {
                var endpoints = Read<StaticClientRuntimeEndpointState[]>(cluster, "_endpoints");
                return endpoints.Any(endpoint => endpoint.Connections.Contains(Connection));
            }
        }

        internal (SharpLinkClient.ContractRouteFailure? Cut, SharpLinkClient.ContractRouteFailure? Detach) CaptureDeferredSlots()
        {
            var cluster = Read<object>(Client, "_cluster");
            lock (Read<Lock>(cluster, "_gate"))
                return (ReadNullable<SharpLinkClient.ContractRouteFailure>(Connection, "_contractRouteCutFailure"),
                    ReadNullable<SharpLinkClient.ContractRouteFailure>(Connection, "_contractRouteDetachFailure"));
        }

        internal void Reserve()
        {
            Ensure(!_reservation && Connection.TryReserveExactCallAdmission(), "one actual exact reservation acquired");
            _reservation = true;
        }
        internal void ReleaseReservation()
        {
            if (!_reservation) return;
            _reservation = false;
            Connection.ReleaseCallAdmissionReservation();
        }
        internal void DuplicateClusterDrain()
        {
            var cluster = Read<object>(Client, "_cluster");
            var method = cluster.GetType().GetMethod("MarkConnectionDraining", BindingFlags.Instance | BindingFlags.Public)
                ?? throw new InvalidOperationException("Actual cluster drain entry missing.");
            try { method.Invoke(cluster, [Connection]); }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
        }

        internal async Task<Owner> CaptureCleanupAsync(string operation)
        {
            await UntilAsync(() => CaptureFramework(operation, "original physical cleanup") == 1,
                "the same required pending physical cleanup Task must be registered");
            return Snapshot().Single(owner => owner.Stage == "original physical cleanup");
        }

        internal async Task AcceptCleanupAsync(Owner cleanup, Exception marker, AggregateException? disposalGraph, bool topologyCommitted)
        {
            var expected = new[] { marker }.Concat(disposalGraph is null ? [] : Leaves(disposalGraph)).ToArray();
            cleanup.Expected = error =>
            {
                Ensure(error is AggregateException { InnerExceptions.Count: 1 } outer &&
                       outer.InnerExceptions[0] is SharpLinkClient.ContractRouteFailure,
                    "the exact required cleanup task faults with an opaque route envelope, never cancellation");
                AssertOccurrences(error, expected);
                var batches = RouteBatches(error).Where(batch => Enumerable.Range(0, batch.Count).Any(index =>
                    batch.StageAt(index) == (topologyCommitted ? "site-reconcile" : "topology-prebuild") &&
                    ReferenceEquals(batch.ErrorAt(index), marker))).ToArray();
                Ensure(batches.Length == 1 && batches[0].TopologyCommitted == topologyCommitted,
                    "the exact marker retains its real precommit/committed fanout owner-stage identity");
                _acceptedCleanupEnvelope = ((AggregateException)error).InnerExceptions[0];
            };
            await ObserveAsync(cleanup);
            // This transport Task is an independent original stage, not deduplicated merely
            // because the required cleanup also retained its fault graph.
            if (Peer.DisposeOwner is { } transport) await ObserveAsync(transport);
        }

        private static IEnumerable<SharpLinkClient.ContractRouteFailure> RouteBatches(Exception error)
        {
            if (error is SharpLinkClient.ContractRouteFailure batch)
            {
                yield return batch;
                for (var index = 0; index < batch.Count; index++)
                    foreach (var nested in RouteBatches(batch.ErrorAt(index))) yield return nested;
            }
            else if (error is AggregateException aggregate)
                foreach (var child in aggregate.InnerExceptions)
                    foreach (var nested in RouteBatches(child)) yield return nested;
        }

        internal Owner StartStop() => _stop ??= Own("original Stop", Client.StopAsync().AsTask());
        internal async Task WaitForSupervisorSealedAsync()
        {
            var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
            await UntilAsync(() =>
            {
                lock (Read<Lock>(supervisor, "_gate")) return Read<bool>(supervisor, "_sealed");
            }, "the actual shutdown supervisor must seal before releasing held disposal");
        }
        internal async Task AcceptLifecycleAsync(Owner owner)
        {
            var exact = _acceptedCleanupEnvelope ?? throw new InvalidOperationException("Original cleanup envelope not yet accepted.");
            owner.Expected = error =>
            {
                Ensure(error is AggregateException { InnerExceptions.Count: 1 } aggregate &&
                       ReferenceEquals(aggregate.InnerExceptions[0], exact),
                    "this separate lifecycle owner preserves the exact required cleanup envelope with no unexpected siblings");
            };
            await ObserveAsync(owner);
        }

        internal async Task CleanupAsync(Exception? primary)
        {
            List<Exception> errors = [];
            if (primary is not null) errors.Add(primary);
            ClearHooks();
            if (_client is not null)
            {
                // Capture an already-registered owner before opening the physical gate, even
                // when the body failed between callback entry and its explicit capture.
                try { CaptureFramework("StaticClusterDisconnectedConnectionCleanup", "original physical cleanup"); }
                catch (Exception error) { errors.Add(error); }
                try { CaptureFramework("StaticClusterIdleConnectionCleanup", "original physical cleanup"); }
                catch (Exception error) { errors.Add(error); }
                try { CaptureFramework("StaticClusterSessionRefreshRetiredConnectionCleanup", "original physical cleanup"); }
                catch (Exception error) { errors.Add(error); }
                try { CaptureFramework("DynamicClusterTopologyRetirementCleanup", "original removed physical cleanup"); }
                catch (Exception error) { errors.Add(error); }
                try { CaptureFramework("DynamicClusterRetiredTopologyRelease", "original removed factory release"); }
                catch (Exception error) { errors.Add(error); }
            }
            foreach (var factory in _factories)
            {
                factory.Transport.ReleaseDisposal();
                factory.ReplacementTransport?.ReleaseDisposal();
                factory.ReleaseFactoryDisposal();
            }
            var launchers = Snapshot().Where(owner => owner.Stage.EndsWith(" launcher", StringComparison.Ordinal)).ToArray();
            foreach (var launcher in launchers) await JoinAsync(launcher, errors);
            if (launchers.Any(owner => !owner.Source.IsCompleted)) { await QuarantineAsync(errors); return; }
            // Original private owner Tasks are inserted by their launchers before those
            // launchers return. They are not supervised coordinator Tasks: independently
            // join their physical producer lifetime before releasing reservations or Stop.
            var direct = Snapshot().Where(owner => owner.Stage.StartsWith("original reflected ", StringComparison.Ordinal)).ToArray();
            foreach (var owner in direct) await JoinAsync(owner, errors);
            if (direct.Any(owner => !owner.Source.IsCompleted)) { await QuarantineAsync(errors); return; }
            try { ReleaseReplacementSentinel(); } catch (Exception error) { errors.Add(error); }
            try { ReleaseReservation(); } catch (Exception error) { errors.Add(error); }
            if (_client is not null)
            {
                // Join scheduler producers before freezing the set of original Bind tasks.
                CaptureFramework("StartContractBinding", "original binding scheduler");
                foreach (var owner in Snapshot().Where(owner => owner.Stage == "original binding scheduler"))
                    await JoinAsync(owner, errors);
                if (Snapshot().Any(owner => owner.Stage == "original binding scheduler" && !owner.Source.IsCompleted))
                { await QuarantineAsync(errors); return; }
                CaptureFramework("ContractBinding", "original Bind");
                for (var index = 0; index < 2; index++)
                {
                    if (!_binds[index].HasValue || _replyStarted[index]) continue;
                    try { await ConfirmAsync(index); } catch (Exception error) { errors.Add(error); }
                }
                try { await ConfirmAdditionalSiteForCleanupAsync(); } catch (Exception error) { errors.Add(error); }
                try { StartStop(); } catch (Exception error) { errors.Add(error); }
                if (_stop is not null)
                {
                    if (_acceptedCleanupEnvelope is not null && !_stop.Reported)
                    {
                        try { await AcceptLifecycleAsync(_stop); } catch (Exception error) { errors.Add(error); }
                    }
                    await JoinAsync(_stop, errors);
                }
                if (_stop is not { Source.IsCompleted: true }) { await QuarantineAsync(errors); return; }
                try
                {
                    var dispose = Own("original Client Dispose", Client.DisposeAsync().AsTask());
                    if (_acceptedCleanupEnvelope is not null) await AcceptLifecycleAsync(dispose);
                    else await JoinAsync(dispose, errors);
                }
                catch (Exception error) { errors.Add(error); }
            }
            // Stop is terminal: no binding, receive worker, reconnect, or cleanup producer
            // can now acquire a new transport. Release factories independently even on body failure.
            List<Owner> finalFactories = [];
            foreach (var factory in _factories)
            {
                try { finalFactories.Add(Own("fixture final factory Dispose " + factory.Name, factory.DisposeAsync().AsTask())); }
                catch (Exception error) { errors.Add(error); }
            }
            // A factory may append its second original peer Dispose after an await. Prove
            // those factory producers terminal before freezing the complete owner collection.
            foreach (var factory in finalFactories) await JoinAsync(factory, errors);
            if (finalFactories.Any(owner => !owner.Source.IsCompleted)) { await QuarantineAsync(errors); return; }
            var final = Snapshot();
            foreach (var owner in final) await JoinAsync(owner, errors);
            if (final.Any(owner => !owner.Source.IsCompleted)) { await QuarantineAsync(errors); return; }
            // Fixed all-terminal snapshot; re-audit late faults after any bounded first join.
            foreach (var owner in final) await JoinAsync(owner, errors);
            Throw(errors);
        }

        private async Task QuarantineAsync(List<Exception> errors)
        {
            lock (Quarantine) Quarantine.Add(this);
            errors.Add(new InvalidOperationException(
                "Unknown original owner exit: all fixture/client/peer/task roots retained. Runner must isolate/terminate this process."));
            foreach (var owner in Snapshot()) if (owner.Source.IsCompleted) await JoinAsync(owner, errors);
            Throw(errors);
        }

        private int CaptureFramework(string operation, string stage)
        {
            var supervisor = Read<FrameworkTaskSupervisor>(Client, "_frameworkTasks");
            lock (Read<Lock>(supervisor, "_gate"))
            {
                foreach (var pair in Read<Dictionary<Task, FrameworkTaskRegistration>>(supervisor, "_active"))
                    if (pair.Value.Operation == operation && !Snapshot().Any(owner =>
                        owner.Stage == stage && ReferenceEquals(owner.Source, pair.Key))) Own(stage, pair.Key);
            }
            return Snapshot().Count(owner => owner.Stage == stage);
        }

        internal Owner Own(string stage, Task task)
        {
            var owner = new Owner(stage, task);
            lock (_ownersGate) _owners.Add(owner);
            return owner;
        }
        private Owner[] Snapshot() { lock (_ownersGate) return _owners.ToArray(); }

        private static async Task UntilAsync(Func<bool> predicate, string message)
        {
            var start = Stopwatch.GetTimestamp();
            while (!predicate())
            {
                if (Stopwatch.GetElapsedTime(start) >= Bound) throw new TimeoutException(message);
                await Task.Delay(1);
            }
        }
        private static async Task ObserveAsync(Owner owner)
        {
            try { await owner.Source.WaitAsync(Bound); }
            catch (Exception observer)
            {
                if (!owner.Source.IsCompleted) throw;
                var original = await CaptureTerminalAsync(owner.Source);
                if (original is null) throw;
                if (owner.Expected is not null)
                {
                    try { owner.Expected(original); }
                    catch (Exception validation)
                    {
                        owner.Reported = true;
                        List<Exception> failures = [validation];
                        if (IsTimeout(owner.Source, observer)) failures.Add(observer);
                        failures.Add(original);
                        throw new AggregateException("Expected-outcome validation plus complete original observation graph.", failures);
                    }
                    owner.Reported = true;
                    if (IsTimeout(owner.Source, observer)) throw new AggregateException(observer, original);
                    return;
                }
                owner.Reported = true;
                if (IsTimeout(owner.Source, observer)) throw new AggregateException(observer, original);
                ExceptionDispatchInfo.Capture(original).Throw();
            }
            Ensure(owner.Expected is null, "expected original failure unexpectedly completed successfully: " + owner.Stage);
        }
        private static async Task JoinAsync(Owner owner, List<Exception> errors)
        {
            try { await owner.Source.WaitAsync(Bound); }
            catch (Exception observer)
            {
                if (owner.Reported) return;
                if (!owner.Source.IsCompleted)
                {
                    errors.Add(new TimeoutException("Original owner has not exited: " + owner.Stage, observer));
                    return;
                }
                var original = await CaptureTerminalAsync(owner.Source);
                if (original is null || IsTimeout(owner.Source, observer)) errors.Add(observer);
                if (original is null) return;
                if (owner.Expected is not null)
                {
                    try { owner.Expected(original); owner.Reported = true; return; }
                    catch (Exception rejected) { errors.Add(rejected); }
                }
                owner.Reported = true;
                errors.Add(original);
            }
        }
        private static async Task<Exception?> CaptureTerminalAsync(Task task)
        {
            Ensure(task.IsCompleted, "a terminal audit cannot become an unbounded source wait");
            try { await task; return null; }
            catch (Exception error) { return task.Exception is { } full ? full : error; }
        }
        private static bool IsTimeout(Task source, Exception error)
            => error is TimeoutException && !(source.Exception?.InnerExceptions.Any(item => ReferenceEquals(item, error)) ?? false);
        private static void Throw(List<Exception> errors)
        {
            if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
            if (errors.Count > 1) throw new AggregateException("Body first; complete independent original stage graphs follow.", errors);
        }

        internal sealed class Owner(string stage, Task source)
        {
            internal string Stage { get; } = stage;
            internal Task Source { get; } = source;
            internal Action<Exception>? Expected;
            internal bool Reported;
        }

        internal sealed class GatedFactory : IClientTransportFactory
        {
            private readonly PhysicalFixture _fixture;
            internal string Name { get; }
            private readonly Lock _factoryDisposeGate = new();
            private readonly TaskCompletionSource _factoryRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Task? _factoryDispose;
            internal bool HoldFactoryDisposal;
            internal TaskCompletionSource FactoryDisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal Owner? FactoryDisposeOwner;
            internal int FactoryDisposalCount;
            internal void ReleaseFactoryDisposal() => _factoryRelease.TrySetResult();
            internal TestClientTransportFactory Peer { get; } = new(
                contractManifest: [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
                acceptManualContractBindings: false);
            internal GatedTransport Transport { get; }
            private TestClientTransportFactory? _replacementPeer;
            internal GatedTransport? ReplacementTransport { get; private set; }
            internal int ConnectCount => Peer.ConnectCount + (_replacementPeer?.ConnectCount ?? 0);
            internal void EnableOneReplacement()
            {
                Ensure(_replacementPeer is null, "exactly one replacement peer is preallocated");
                _replacementPeer = new(contractManifest:
                    [new(ManualClientContractFixture.ContractId, ManualClientContractFixture.AssemblyHash)],
                    acceptManualContractBindings: false);
                ReplacementTransport = new(_fixture, _replacementPeer.Connection, Name + " replacement", held: true);
            }
            internal GatedFactory(PhysicalFixture fixture, string name, bool held)
            {
                _fixture = fixture;
                Name = name;
                Transport = new(fixture, Peer.Connection, name, held);
            }
            public async ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            {
                var peer = Peer.ConnectCount == 0 ? Peer : _replacementPeer;
                Ensure(peer is not null && peer.ConnectCount == 0,
                    "manual clock must prevent any unallocated/repeated physical dial");
                var task = peer!.ConnectAsync(cancellationToken).AsTask();
                _fixture.Own("original peer handshake " + Name, task);
                await task;
                return ReferenceEquals(peer, Peer) ? Transport : ReplacementTransport!;
            }
            public ValueTask DisposeAsync()
            {
                lock (_factoryDisposeGate)
                {
                    if (_factoryDispose is null)
                    {
                        _factoryDispose = DisposeFactoryCoreAsync();
                        FactoryDisposeOwner = _fixture.Own("original factory Dispose " + Name, _factoryDispose);
                    }
                    return new(_factoryDispose);
                }
            }
            private async Task DisposeFactoryCoreAsync()
            {
                Interlocked.Increment(ref FactoryDisposalCount);
                FactoryDisposalEntered.TrySetResult();
                if (HoldFactoryDisposal) await _factoryRelease.Task;
                if (_replacementPeer is not null)
                {
                    await DisposeBothPeersAsync();
                    return;
                }
                var original = Peer.DisposeAsync().AsTask();
                _fixture.Own("original factory-owned peer Dispose " + Name, original);
                await original;
            }
            private async Task DisposeBothPeersAsync()
            {
                List<Exception> errors = [];
                foreach (var peer in new[] { Peer, _replacementPeer! })
                {
                    Task? original = null;
                    try { original = peer.DisposeAsync().AsTask(); }
                    catch (Exception error) { errors.Add(error); }
                    if (original is null) continue;
                    _fixture.Own("original factory-owned peer Dispose " + Name, original);
                    try { await original; }
                    catch (Exception error) { errors.Add(original.Exception is { } graph ? graph : error); }
                }
                Throw(errors);
            }
        }

        internal sealed class GatedTransport : ITransportConnection
        {
            private readonly PhysicalFixture _fixture;
            private readonly TestTransportConnection _inner;
            private readonly string _name;
            private readonly Lock _gate = new();
            private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            private Task? _disposal;
            private int _disposals;
            internal TaskCompletionSource DisposalEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            internal AggregateException? DisposeFailure;
            internal Owner? DisposeOwner;
            internal Task? InnerDispose;
            internal int DisposalCount => Volatile.Read(ref _disposals);
            internal GatedTransport(PhysicalFixture fixture, TestTransportConnection inner, string name, bool held)
            {
                _fixture = fixture; _inner = inner; _name = name;
                if (!held) _release.TrySetResult();
            }
            public string Id => _inner.Id;
            public PipeReader Input => _inner.Input;
            public PipeWriter Output => _inner.Output;
            public EndPoint? LocalEndPoint => _inner.LocalEndPoint;
            public EndPoint? RemoteEndPoint => _inner.RemoteEndPoint;
            internal void ReleaseDisposal() => _release.TrySetResult();
            public ValueTask DisposeAsync()
            {
                lock (_gate)
                {
                    if (_disposal is null)
                    {
                        _disposal = DisposeCoreAsync();
                        DisposeOwner = _fixture.Own("original physical transport Dispose " + _name, _disposal);
                        if (DisposeFailure is { } exact)
                            DisposeOwner.Expected = error =>
                            {
                                Ensure(error is AggregateException { InnerExceptions.Count: 1 } outer &&
                                       ReferenceEquals(outer.InnerExceptions[0], exact),
                                    "independent original transport task retains its exact supplied Aggregate graph");
                                AssertOccurrences(error, Leaves(exact).ToArray());
                            };
                    }
                    return new(_disposal);
                }
            }
            private async Task DisposeCoreAsync()
            {
                Interlocked.Increment(ref _disposals);
                DisposalEntered.TrySetResult();
                await _release.Task;
                InnerDispose = _inner.DisposeAsync().AsTask();
                _fixture.Own("original inner peer Dispose " + _name, InnerDispose);
                await InnerDispose;
                if (DisposeFailure is { } error) throw error;
            }
        }
    }

    private static T Read<T>(object owner, string field)
        => (T)(Field(owner, field).GetValue(owner) ?? throw new InvalidOperationException("Missing value: " + field));
    private static T? ReadNullable<T>(object owner, string field) where T : class
        => (T?)Field(owner, field).GetValue(owner);
    private static FieldInfo Field(object owner, string field)
        => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new InvalidOperationException($"Missing exact source field {owner.GetType().Name}.{field}.");
}
