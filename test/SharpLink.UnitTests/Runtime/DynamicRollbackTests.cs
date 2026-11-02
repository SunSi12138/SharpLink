using System.Net;
using System.Reflection;
using System.Runtime.Loader;
using System.Threading;
using System.Collections.Generic;
using SharpLink.Client;
using SharpLink.RollbackPlugin;
using SharpLink.Server;

namespace SharpLink.UnitTests.Runtime;

// Every test in this fixture coordinates through RollbackState and SHARPLINK_ROLLBACK_* process state.
[NotInParallel("rollback-plugin")]
public class DynamicRollbackTests
{
    [Test]
    public async Task HugeDynamicDrainTimeoutShouldRemainPendingUntilLeaseRelease(CancellationToken testCancellationToken)
    {
        await B3aOwner.Run(false, false, testCancellationToken, async owner =>
        {
            var unregister = await owner.Unregister(TimeSpan.MaxValue);
            await Task.Delay(50);
            var completedBeforeDrain = unregister.IsCompleted;
            owner.ReleaseLease();
            var result = await owner.Result(unregister);
            Ensure(!completedBeforeDrain,
                "a huge positive graceful timeout must not overflow the native delay range");
            Ensure(result is { ReferencesReleased: true },
                "the unregister operation must complete after its active lease drains");
            owner.AssertCounters();
        });
    }

    [Test]
    public async Task ClientUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider(CancellationToken testCancellationToken)
    {
        await B3aOwner.Run(false, true, testCancellationToken, owner => owner.CheckProviderDeadline());
    }

    [Test]
    public async Task ServerUnregisterRetainedLeaseShouldUseOnlyItsRuntimeContextProvider(CancellationToken testCancellationToken)
    {
        await B3aOwner.Run(true, true, testCancellationToken, owner => owner.CheckProviderDeadline());
    }

    [Test]
    public async Task ClientRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await B3bOwner.Run(false, testCancellationToken);
    }

    [Test]
    public async Task ServerRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await B3bOwner.Run(true, testCancellationToken);
    }

    [Test]
    public async Task ClientReplacementRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await B3cOwner.Run(false, testCancellationToken);
    }

    [Test]
    public async Task ServerReplacementRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await B3cOwner.Run(true, testCancellationToken);
    }

    [Test]
    public async Task ServerProfileBindingFailureShouldDisposeRuntimeContextAndPreserveBothFailures(CancellationToken testCancellationToken)
    {
        var owner = new SharpLink.UnitTests.Builder.RollbackBuildFixture();
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            owner.Initialize("server-build-schema");
            var transport = owner.CreateTransport(bindingFailure: "server profile binding failed");
            var builder = SharpLinkServerBuilder.Create()
                .UseGeneratedManifestSource(new FixedGeneratedManifestSource([owner.Manifest]))
                .UseTransport(transport);
            var failure = owner.CaptureBuild(builder);

            Ensure(Contains(failure, "server profile binding failed"), "Server build retains profile failure");
            Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server build retains Context cleanup failure");
            Ensure(RollbackState.ScopeDisposeCount == 1, "Server build disposes Runtime Context once");
            var receipts = owner.Manifest.GetScopeCleanupReceipts();
            Ensure(receipts.Length == 1 && receipts[0].Count == 1 && receipts[0].Order == 1 && transport.DisposeCount == 1,
                "profile rollback must dispose its exact scope and listener once");
            Ensure(failure is AggregateException aggregate && aggregate.InnerExceptions.Count == 2 &&
                ReferenceEquals(aggregate.InnerExceptions[0], transport.BindingFailure) &&
                ReferenceEquals(aggregate.InnerExceptions[1], receipts[0].Failure),
                "profile rollback retains exactly primary then scope cleanup, without unknown siblings");
            owner.AcceptBuildFailure();
        }
        catch (Exception failure) { owner.RecordBodyFailure(failure); }
        finally { await owner.FinishAsync(testCancellationToken); }
        owner.ThrowIfFailed();
    }

    // B3b owns the real public registration invocations and exact instance receipts.
    // It is intentionally separate from B3a's no-cleanup-fault normal-exit owner.
    private sealed class B3cOwner(bool server)
    {
        private static readonly Lock RootsGate = new();
        private static readonly HashSet<B3cOwner> Roots = [];
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);
        private static readonly string[] EnvironmentKeys = ["SHARPLINK_ROLLBACK_SCHEMA", "SHARPLINK_ROLLBACK_CODEC_IDENTITY",
            "SHARPLINK_ROLLBACK_DISABLE_CODEC", "SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT"];
        private readonly string?[] _previousEnvironment = new string?[EnvironmentKeys.Length];
        private readonly bool[] _writeAttempted = new bool[EnvironmentKeys.Length];
        private readonly Lock _gate = new();
        private readonly Dictionary<Task, Task<B3cOutcome>> _outcomes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Task> _accepted = new(ReferenceEqualityComparer.Instance);
        private readonly List<(string Stage, Exception Failure)> _failures = [];
        private readonly List<(string Stage, Task Original)> _taskEdges = [];
        private readonly List<Exception> _hookFailures = [];
        private readonly List<ISharpLinkGeneratedAssemblyManifest> _observedManifests = [];
        private readonly B3bTransport _transport = new();
        private bool _permit, _snapshots, _offered, _registrationAttempted, _hookSnapshot, _hookWriteAttempted;
        private object? _builder;
        private IAsyncDisposable? _endpoint;
        private SharpLinkClient? _client;
        private SharpLinkServer? _server;
        private SharpLinkRuntimeContext? _runtime;
        private ClientAssemblyRegistry? _clientRegistry;
        private ServerServiceModuleRegistry? _serverRegistry;
        private SharpLinkDynamicModule? _retainedModule;
        private RpcGeneratedManifestRegistration? _retainedCodecOwner;
        private ISharpLinkGeneratedAssemblyManifest? _retainedManifest, _incomingManifest;
        private Exception? _retainedFailure, _incomingFailure;
        private Task? _setup, _replacementInvocation, _stopInvocation, _stop, _disposeInvocation, _dispose, _terminal;
        private B3aWave? _retainedWave1, _retainedWave2;
        private PluginLoadContext? _context, _replacementOldContext;
        private Assembly? _replacementOldAssembly;
        private SharpLinkDynamicModule? _replacementOldModule;
        private RpcGeneratedManifestRegistration? _replacementOldCodecOwner;
        private ISharpLinkGeneratedAssemblyManifest? _replacementOldManifest;
        private B3aWave? _oldWave1, _oldWave2;
        private bool _replacementOldRegistrationAttempted, _replacementApiReturned;
        private Task<SharpLinkAssemblyReplacementResult>? _replacementReturned;
        private ValueTask<SharpLinkAssemblyReplacementResult>? _replacementValue;
        private Assembly? _incomingAssembly;
        private FieldInfo? _hookField;
        private object? _previousHook, _defaultObserver, _defaultStarted, _defaultRelease;
        private Action<ISharpLinkGeneratedAssemblyManifest>? _installedHook;
        private bool _hookRestored, _oracleComplete;
        private readonly Assembly _retainedAssembly = typeof(RollbackMarker).Assembly;

        internal static async Task Run(bool server, CancellationToken token)
        {
            var owner = new B3cOwner(server);
            lock (RootsGate) Roots.Add(owner);
            try
            {
                await RollbackState.TestIsolation.WaitAsync(token);
                owner._permit = true;
                try
                {
                    owner.PrepareEnvironment();
                    owner._setup = owner.Own(Task.Run(owner.Initialize), "setup");
                    await owner.Success(owner._setup, "setup");
                    owner._replacementInvocation = owner.Own(Task.Run(owner.ReplaceIncoming), "replacement-invocation");
                    await owner.CheckRollback();
                }
                catch (Exception failure) { owner.Fail("body", failure); }
                finally
                {
                    try { await owner.Close(); }
                    catch (Exception failure) { owner.Fail("close", failure); }
                }
            }
            catch (Exception failure) { owner.Fail("admission", failure); }
            finally
            {
                if (!owner._permit && owner._endpoint is null && !owner._offered)
                    lock (RootsGate) Roots.Remove(owner);
            }
            Exception[] failures;
            lock (owner._gate) failures = owner._failures.ConvertAll(x => x.Failure).ToArray();
            if (failures.Length != 0) throw new AggregateException(failures);
        }

        private void Fail(string stage, Exception failure) { lock (_gate) _failures.Add((stage, failure)); }
        private void Require(bool condition, string message) => Ensure(condition, "B3c " + message);
        private static object? ReadField(object owner, string name)
            => (owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing B3c field " + name)).GetValue(owner);
        private static T Field<T>(object owner, string name) => (T)(ReadField(owner, name)
            ?? throw new InvalidOperationException("Missing B3c owner " + name));

        private void PrepareEnvironment()
        {
            // Read every prior value before any mutation. Foreign default hooks
            // are not invoked, cleared or chained by this fixture.
            for (var i = 0; i < EnvironmentKeys.Length; i++)
                _previousEnvironment[i] = Environment.GetEnvironmentVariable(EnvironmentKeys[i]);
            _snapshots = true;
            _defaultObserver = RollbackState.ManifestConstructed;
            _defaultStarted = RollbackState.ManifestConstructionStarted;
            _defaultRelease = RollbackState.ManifestConstructionRelease;
            Require(_defaultObserver is null && _defaultStarted is null && _defaultRelease is null,
                "default assembly has no foreign observer or construction barrier");
            for (var i = 0; i < EnvironmentKeys.Length; i++)
            {
                _writeAttempted[i] = true;
                Environment.SetEnvironmentVariable(EnvironmentKeys[i], i == 0 ? "retained-schema" : null);
            }
        }

        private void Initialize()
        {
            if (server)
            {
                var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
                _builder = builder; _offered = true; builder.UseTransport(_transport);
                var endpoint = builder.Build(); _endpoint = endpoint; _server = (SharpLinkServer)endpoint;
                _runtime = Field<SharpLinkRuntimeContext>(_server, "_runtimeContext");
                _serverRegistry = Field<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry");
                _registrationAttempted = true;
                Require(endpoint.RegisterAssembly(_retainedAssembly).Succeeded, "first Server registration");
            }
            else
            {
                var builder = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).DisableRequestTimeout();
                _builder = builder; _offered = true; builder.UseTransport(_transport);
                var endpoint = builder.Build(); _endpoint = endpoint; _client = (SharpLinkClient)endpoint;
                _runtime = (SharpLinkRuntimeContext)_client.RuntimeContext;
                _clientRegistry = Field<ClientAssemblyRegistry>(_client, "_assemblyRegistry");
                _registrationAttempted = true;
                Require(endpoint.RegisterAssembly(_retainedAssembly).Succeeded, "first Client registration");
            }
            CaptureModule();
            _retainedCodecOwner = _retainedModule!.CodecRegistration;
            _retainedManifest = ((PreparedGeneratedManifest)_retainedModule!.Manifest).OriginalManifest;
            Require(_retainedManifest is RollbackManifest && ReferenceEquals(_retainedManifest.OwnerAssembly, _retainedAssembly), "exact first instance");
            var oldReceipt = Receipts(_retainedManifest);
            Require(oldReceipt.Length == 1 && oldReceipt[0].Count == 0 && oldReceipt[0].Order == 0, "first scope not yet disposed");
            _retainedFailure = oldReceipt[0].Failure;
            // The old replacement target is a different real module, deliberately
            // codec-free. The retained default module remains the conflict owner.
            _replacementOldContext = new PluginLoadContext(server ? "b3c-server-old" : "b3c-client-old");
            _replacementOldAssembly = _replacementOldContext.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location);
            var oldState = _replacementOldAssembly.GetType(typeof(RollbackState).FullName!, throwOnError: true)!;
            Require(oldState.GetField(nameof(RollbackState.ManifestConstructed))!.GetValue(null) is null &&
                oldState.GetField(nameof(RollbackState.ManifestConstructionStarted))!.GetValue(null) is null &&
                oldState.GetField(nameof(RollbackState.ManifestConstructionRelease))!.GetValue(null) is null,
                "old collectible assembly has no foreign observer/barrier");
            _writeAttempted[2] = true;
            Environment.SetEnvironmentVariable(EnvironmentKeys[2], "1");
            _replacementOldRegistrationAttempted = true;
            var oldRegistration = server ? _server!.RegisterAssembly(_replacementOldAssembly)
                : _client!.RegisterAssembly(_replacementOldAssembly);
            Require(oldRegistration.Succeeded, "old codec-free registration");
            CaptureReplacementOldModule();
            _replacementOldCodecOwner = _replacementOldModule!.CodecRegistration;
            _replacementOldManifest = ((PreparedGeneratedManifest)_replacementOldModule.Manifest).OriginalManifest;
            Require(ReferenceEquals(_replacementOldManifest.OwnerAssembly, _replacementOldAssembly) &&
                _replacementOldManifest.Codecs.Count == 0 && Receipts(_replacementOldManifest).Length == 0,
                "actual old module has no codec factories or scope receipts");
            Environment.SetEnvironmentVariable(EnvironmentKeys[2], null);
            _context = new PluginLoadContext(server ? "b3c-server-incoming" : "b3c-client-incoming");
            _incomingAssembly = _context.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location);
            var state = _incomingAssembly.GetType(typeof(RollbackState).FullName!, throwOnError: true)!;
            Require(state.GetField(nameof(RollbackState.ManifestConstructionStarted))!.GetValue(null) is null &&
                state.GetField(nameof(RollbackState.ManifestConstructionRelease))!.GetValue(null) is null, "incoming has no foreign barrier");
            _hookField = state.GetField(nameof(RollbackState.ManifestConstructed))!;
            _previousHook = _hookField.GetValue(null); _hookSnapshot = true;
            _installedHook = ObserveIncoming;
            _hookWriteAttempted = true;
            _hookField.SetValue(null, _installedHook);
            _writeAttempted[0] = true;
            Environment.SetEnvironmentVariable(EnvironmentKeys[0], "incoming-schema");
        }

        private void ObserveIncoming(ISharpLinkGeneratedAssemblyManifest manifest)
        {
            // Root actual this BEFORE validating. A callback throw/constructor
            // non-return cannot discard the only instance receipt owner.
            lock (_gate)
            {
                var previous = _incomingManifest;
                _observedManifests.Add(manifest);
                _incomingManifest ??= manifest;
                try
                {
                    Require(previous is null, "one actual incoming construction");
                    Require(ReferenceEquals(manifest.OwnerAssembly, _incomingAssembly), "incoming observer exact assembly");
                }
                catch (Exception failure) { _hookFailures.Add(failure); throw; }
            }
        }

        private void ReplaceIncoming()
        {
            // Keep the API-return boundary separate from conversion. A sync API
            // throw, an AsTask throw and a returned original are distinct facts.
            var pending = server ? _server!.ReplaceAssemblyAsync(_replacementOldAssembly!, _incomingAssembly!, TimeSpan.Zero)
                : _client!.ReplaceAssemblyAsync(_replacementOldAssembly!, _incomingAssembly!, TimeSpan.Zero);
            _replacementValue = pending; // retain the actual value/source if conversion itself fails
            _replacementApiReturned = true;
            var original = pending.AsTask(); // consume the actual ValueTask once
            _replacementReturned = Own(original, "replacement-returned");
        }

        private static (Exception Failure, int Count, int Order)[] Receipts(ISharpLinkGeneratedAssemblyManifest manifest)
            => ((Exception Failure, int Count, int Order)[])(manifest.GetType()
                .GetMethod(nameof(RollbackManifest.GetScopeCleanupReceipts), BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(manifest, null) ?? throw new InvalidOperationException("Missing actual scope receipt"));

        private void WithRegistry(Action action)
        {
            if (_serverRegistry is not null) { lock (_serverRegistry.Gate) action(); }
            else if (_clientRegistry is not null) { lock (Field<Lock>(_clientRegistry, "_gate")) action(); }
            else throw new InvalidOperationException("Unknown B3c registry");
        }

        private void CaptureModule()
        {
            if (_retainedModule is not null) return;
            WithRegistry(() =>
            {
                var found = _serverRegistry is not null ? _serverRegistry.DynamicModules.TryGetValue(_retainedAssembly, out _retainedModule)
                    : _clientRegistry!.DynamicModules.TryGetValue(_retainedAssembly, out _retainedModule);
                Require(found && _retainedModule is not null, "exact first module root");
            });
        }

        private void CaptureReplacementOldModule()
        {
            if (_replacementOldModule is not null) return;
            WithRegistry(() =>
            {
                var found = _serverRegistry is not null
                    ? _serverRegistry.DynamicModules.TryGetValue(_replacementOldAssembly!, out _replacementOldModule)
                    : _clientRegistry!.DynamicModules.TryGetValue(_replacementOldAssembly!, out _replacementOldModule);
                Require(found && _replacementOldModule is not null, "exact codec-free old module root");
            });
        }

        private sealed record B3cOutcome(Exception? AwaitFailure, AggregateException? FullTaskFailure)
        {
            internal bool Succeeded => AwaitFailure is null && FullTaskFailure is null;
        }
        private static async Task<B3cOutcome> Observe(Task source)
        {
            try { await source.ConfigureAwait(false); return new(null, null); }
            catch (Exception failure) { return new(failure, source.Exception); }
        }
        private T Own<T>(T source, string stage) where T : Task
        {
            lock (_gate)
            {
                _taskEdges.Add((stage, source));
                if (!_outcomes.ContainsKey(source)) _outcomes.Add(source, Observe(source));
            }
            return source;
        }
        private async Task<B3cOutcome> Outcome(Task source, string stage)
        {
            _ = Own(source, stage);
            Task<B3cOutcome> observer;
            lock (_gate) observer = _outcomes[source];
            return await observer.WaitAsync(Bound); // diagnostic error is NOT source error data
        }
        private void Accept(Task source) { lock (_gate) _accepted.Add(source); }
        private async Task Success(Task source, string stage)
        {
            var outcome = await Outcome(source, stage);
            if (!outcome.Succeeded)
                throw new AggregateException("Unexpected B3c source failure: " + stage,
                    outcome.FullTaskFailure ?? outcome.AwaitFailure!);
            Accept(source);
        }
        private static bool SingleTaskFault(B3cOutcome outcome)
            => outcome.AwaitFailure is not null && outcome.FullTaskFailure is { InnerExceptions.Count: 1 } &&
                ReferenceEquals(outcome.FullTaskFailure.InnerExceptions[0], outcome.AwaitFailure);

        private async Task CheckRollback()
        {
            var outcome = await Outcome(_replacementInvocation!, "replacement-invocation-oracle");
            Require(!_replacementApiReturned && _replacementValue is null && _replacementReturned is null,
                "selected precommit rollback throws synchronously before any API ValueTask return");
            Require(_incomingManifest is not null && _retainedManifest is not null && _replacementOldManifest is not null,
                "three actual manifest instances");
            Require(Receipts(_replacementOldManifest!).Length == 0 && _replacementOldManifest!.Codecs.Count == 0,
                "codec-free old registration unchanged");
            var incoming = Receipts(_incomingManifest!); var old = Receipts(_retainedManifest!);
            Require(incoming.Length == 1 && incoming[0].Count == 1 && incoming[0].Order == 1, "incoming scope rollback exactly once");
            _incomingFailure = incoming[0].Failure;
            Require(old.Length == 1 && old[0].Count == 0 && old[0].Order == 0 && ReferenceEquals(old[0].Failure, _retainedFailure), "original scope unchanged");
            var firstCodecs = _retainedManifest!.Codecs; var incomingCodecs = _incomingManifest!.Codecs;
            Require(firstCodecs.Count == 1 && incomingCodecs.Count == 1 && firstCodecs[0].TargetType == typeof(string) &&
                incomingCodecs[0].TargetType == typeof(string) && !firstCodecs[0].CodecHash.IsEmpty &&
                !incomingCodecs[0].CodecHash.IsEmpty && firstCodecs[0].CodecHash != incomingCodecs[0].CodecHash, "actual distinct string codec identities");
            var expected = $"CodecConflict: Codec conflict for '{typeof(string).FullName}': existing CodecHash '{firstCodecs[0].CodecHash}', incoming CodecHash '{incomingCodecs[0].CodecHash}'.";
            Require(SingleTaskFault(outcome) && outcome.AwaitFailure is AggregateException { InnerExceptions.Count: 2 }, "exact two-part synchronous replacement failure");
            var failure = (AggregateException)outcome.AwaitFailure!;
            Require(failure.InnerExceptions[0].GetType() == typeof(InvalidOperationException) && failure.InnerExceptions[0].InnerException is null &&
                failure.InnerExceptions[0].Message == expected && ReferenceEquals(failure.InnerExceptions[1], _incomingFailure),
                "structured exact conflict then original incoming adapter failure, without unknown siblings");
            Require(!ReferenceEquals(_retainedFailure, _incomingFailure), "different instances own different original failures");
            WithRegistry(() =>
            {
                Require(_retainedModule!.State == SharpLinkDynamicModuleState.Running && ReferenceEquals(_retainedModule.CodecRegistration, _retainedCodecOwner), "retained module remains Running and identical");
                Require(_replacementOldModule!.State == SharpLinkDynamicModuleState.Running &&
                    ReferenceEquals(_replacementOldModule.CodecRegistration, _replacementOldCodecOwner) &&
                    ReferenceEquals(((PreparedGeneratedManifest)_replacementOldModule.Manifest).OriginalManifest, _replacementOldManifest),
                    "old replacement target remains Running with its exact codec-free registration");
                Require(!ReferenceEquals(_retainedModule, _replacementOldModule), "two distinct real modules");
                foreach (var original in new[] { _retainedModule, _replacementOldModule })
                    Require(original!.RetirementControl is null && original.RetirementOperation is null &&
                        original.RetirementTracking is null && original.RetirementObservation is null &&
                        original.RetirementRegistration is null && original.CleanupCompletion is null &&
                        !original.ForcedCancellation.IsCancellationRequested,
                        "failed preparation did not select retirement or cancel either live module");
                SharpLinkDynamicModule? retained, oldTarget;
                Require(_serverRegistry is not null
                    ? _serverRegistry.DynamicModules.TryGetValue(_retainedAssembly, out retained) && ReferenceEquals(retained, _retainedModule) &&
                      _serverRegistry.DynamicModules.TryGetValue(_replacementOldAssembly!, out oldTarget) && ReferenceEquals(oldTarget, _replacementOldModule)
                    : _clientRegistry!.DynamicModules.TryGetValue(_retainedAssembly, out retained) && ReferenceEquals(retained, _retainedModule) &&
                      _clientRegistry.DynamicModules.TryGetValue(_replacementOldAssembly!, out oldTarget) && ReferenceEquals(oldTarget, _replacementOldModule),
                    "both exact module identities remain published");
                Require(_serverRegistry is not null ? !_serverRegistry.DynamicModules.ContainsKey(_incomingAssembly!) &&
                    !_serverRegistry.UnregisterOperations.ContainsKey(_incomingAssembly!) :
                    !_clientRegistry!.DynamicModules.ContainsKey(_incomingAssembly!) && !ClientOperations().ContainsKey(_incomingAssembly!),
                    "failed incoming assembly has no published or retiring module");
            });
            lock (_gate) Require(_hookFailures.Count == 0, "constructor observer had no failure");
            Accept(_replacementInvocation!);
            _oracleComplete = true;
        }

        private Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)> ClientOperations()
            => Field<Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)>>(_clientRegistry!, "_unregisterOperations");

        private async Task Attempt(string stage, Func<Task> action)
        {
            try { await action(); } catch (Exception failure) { Fail(stage, failure); }
        }
        private void Check(string stage, Action action)
        {
            try { action(); } catch (Exception failure) { Fail(stage, failure); }
        }
        private async Task KnownFault(Task task, string stage, Func<Exception, bool> matches)
        {
            var outcome = await Outcome(task, stage);
            if (!SingleTaskFault(outcome) || !matches(outcome.AwaitFailure!))
            {
                var rejected = new InvalidOperationException("B3c exact known fault mismatch at " + stage);
                if (outcome.FullTaskFailure is { } full) throw new AggregateException(rejected, full);
                if (outcome.AwaitFailure is { } raw) throw new AggregateException(rejected, raw);
                throw rejected;
            }
            Accept(task);
        }
        private void Data(string stage, Exception? failure)
        {
            if (failure is not null) Fail(stage, failure);
        }

        private bool IsRetainedPhysical(Exception error)
            => error is PhysicalTaskFailureException physical && _retainedFailure is not null && ReferenceEquals(physical.RawException, _retainedFailure);

        private B3aWave CaptureWave(SharpLinkDynamicModule module)
        {
            B3aWave? wave = null;
            WithRegistry(() => wave = new B3aWave(module));
            if (wave!.Control is not null) _ = Own(wave.Control.Driver.Task, "driver");
            if (wave.Physical is not null) _ = Own(wave.Physical, "physical");
            if (wave.Tracking is not null) _ = Own(wave.Tracking, "tracking");
            if (wave.Observer is not null) _ = Own(wave.Observer, "retirement-observer");
            if (wave.Cleanup is not null) _ = Own(wave.Cleanup, "module-cleanup");
            return wave;
        }

        private async Task InvokeHost(bool dispose)
        {
            var invocation = Task.Factory.StartNew(() =>
            {
                var source = dispose ? _endpoint!.DisposeAsync().AsTask() : _endpoint is ISharpLinkServer host
                    ? host.StopAsync(TimeSpan.Zero).AsTask() : ((ISharpLinkClient)_endpoint!).StopAsync().AsTask();
                _ = Own(source, dispose ? "dispose" : "stop");
                if (dispose) _dispose = source; else _stop = source;
                return source;
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            if (dispose) _disposeInvocation = Own(invocation, "dispose-invocation");
            else _stopInvocation = Own(invocation, "stop-invocation");
            await Success(invocation, "host-invocation");
            var original = await invocation;
            if (_retainedFailure is null) await Success(original, "unregistered-host-cleanup");
            else await KnownFault(original, dispose ? "dispose" : "stop", server ? IsRetainedPhysical : e => ReferenceEquals(e, _retainedFailure));
        }

        private async Task JoinWave(B3aWave wave, bool final, bool retained)
        {
            if (wave.Control is null)
                Check("wave", () => Require(!final && wave.Physical is null && wave.Tracking is null && wave.Observer is null,
                    "no missing final control"));
            else
            {
                Check("wave-shape", () => Require(wave.Control.Origin == RetirementOrigin.Stop && wave.Physical is not null &&
                    wave.Tracking is not null && wave.Observer is not null, "complete Stop-origin owner"));
                await Attempt("driver", async () =>
                {
                    await Success(wave.Control.Driver.Task, "driver"); var value = wave.Control.Driver.Task.Result;
                    Data("driver-primary", value.PrimaryFailure); Data("driver-cancellation", value.CancellationFailure);
                    Data("driver-timer", value.TimerFailure); Data("driver-wake", value.WakeFailure);
                    Require(!value.Aborted && !value.HasRetainedTimer, "driver outcome success and real timer exit");
                });
            }
            // Even an incomplete owner shape must not hide another already
            // rooted source or successful Task's independent failure-data slot.
            if (wave.Physical is not null) await Attempt("physical", async () =>
            {
                if (retained) await KnownFault(wave.Physical, "retained-physical", IsRetainedPhysical);
                else
                {
                    await Success(wave.Physical, "codec-free-physical");
                    Require(wave.Physical.Result.ReferencesReleased, "codec-free module physical references released");
                }
            });
            if (wave.Tracking is not null) await Attempt("tracking", async () =>
            {
                await Success(wave.Tracking, "tracking"); Data("tracking-data", wave.Tracking.Result);
            });
            if (wave.Observer is not null) await Attempt("retirement-observer", async () =>
            {
                await Success(wave.Observer, "retirement-observer"); var value = wave.Observer.Result;
                Data("observer-refund-data", value.Failure);
                Require(!value.SupervisorOwned && wave.Receipt is null, "observer refund success/Stop ownership");
            });
            if (final) Check("cleanup-shape", () => Require(wave.Cleanup is not null, "actual cleanup outcome source"));
            if (wave.Cleanup is not null) await Attempt("module-cleanup", async () =>
            {
                await Success(wave.Cleanup, "module-cleanup");
                if (retained ? !ReferenceEquals(wave.Cleanup.Result, _retainedFailure) : wave.Cleanup.Result is not null)
                {
                    Data("unexpected-module-cleanup-data", wave.Cleanup.Result);
                    Require(false, "cleanup data is retained scope failure or codec-free success, by exact module");
                }
            });
        }

        private async Task ServerTails()
        {
            if (_server is null) return;
            var lifecycle = Field<SharpLinkServer.ServerLifecycleCoordinator>(_server, "_lifecycle");
            _terminal = Own(lifecycle.WaitForShutdownAsync(CancellationToken.None), "terminal-completion");
            var service = lifecycle.DeferredServiceCleanupTaskForDiagnostics;
            var serviceObserver = (Task?)ReadField(lifecycle, "_serviceCleanupObserver");
            var shutdownObserver = lifecycle.ShutdownCleanupObserverTaskForDiagnostics;
            var terminalObserver = lifecycle.TerminalFailureObserverTaskForDiagnostics;
            var strictObserver = lifecycle.StrictShutdownCleanupObserverForDiagnostics;
            var strict = lifecycle.StrictPhysicalDrainForDiagnostics;
            foreach (var source in new[] { service, serviceObserver, shutdownObserver, terminalObserver, strictObserver, strict.Drain })
                if (source is not null) _ = Own(source, "server-tail");
            if (service is not null) await Attempt("service-cleanup", () => KnownFault(service, "service-cleanup", IsRetainedPhysical));
            else Check("service-cleanup", () => Require(_retainedModule is null, "normal registered host has actual service cleanup"));
            foreach (var source in new[] { serviceObserver, shutdownObserver, terminalObserver, strictObserver, strict.Drain })
                if (source is not null) await Attempt("server-observer", () => Success(source, "server-observer"));
            await Attempt("terminal-completion", () => KnownFault(_terminal!, "terminal-completion", error =>
                IsRetainedPhysical(error) && _stop is { IsFaulted: true } && ReferenceEquals(error, _stop.Exception!.InnerExceptions[0])));
            var final = lifecycle.StrictPhysicalDrainForDiagnostics;
            Data("strict-drain-data", final.Failure);
            var terminalFailure = lifecycle.StrictPhysicalTerminalFailureForDiagnostics;
            var terminalMatches = _stop is { IsFaulted: true } &&
                ReferenceEquals(terminalFailure, _stop.Exception!.InnerExceptions[0]);
            if (!terminalMatches) Data("unexpected-terminal-data", terminalFailure);
            Check("server-late-tail", () => Require(ReferenceEquals(service, lifecycle.DeferredServiceCleanupTaskForDiagnostics) &&
                ReferenceEquals(serviceObserver, ReadField(lifecycle, "_serviceCleanupObserver")) &&
                ReferenceEquals(shutdownObserver, lifecycle.ShutdownCleanupObserverTaskForDiagnostics) &&
                ReferenceEquals(terminalObserver, lifecycle.TerminalFailureObserverTaskForDiagnostics) &&
                ReferenceEquals(strictObserver, lifecycle.StrictShutdownCleanupObserverForDiagnostics) && ReferenceEquals(strict.Drain, final.Drain),
                "no late uncaptured server tail"));
            Check("service-observer-path", () => Require(serviceObserver is null, "normal Created cleanup did not defer its service observer"));
            Check("strict-exit", () => Require(!final.Requested || final.Observed, "strict drain outcome"));
            Check("terminal-data", () => Require(terminalMatches, "actual terminal failure equals actual Stop wrapper"));
            Check("service-stop-identity", () => Require(service is { IsFaulted: true } && _stop is { IsFaulted: true } &&
                ReferenceEquals(service.Exception!.InnerExceptions[0], _stop.Exception!.InnerExceptions[0]),
                "Stop preserves exact service-cleanup sealed wrapper"));
            Check("server-terminal-state", () => Require(lifecycle.LifecycleState == SharpLinkServerLifecycleState.Faulted,
                "intentional old scope cleanup leaves Server Faulted"));
        }

        private async Task Close()
        {
            GC.KeepAlive(_builder);
            if (_setup is not null) await Attempt("setup-join", () => Success(_setup, "setup-join"));
            if (_setup is { IsCompleted: false }) return; // may still publish host/hook
            if (_replacementInvocation is not null)
            {
                // Keep complete outcome even when the business oracle did not
                // accept it. Never reclassify an arbitrary caught container.
                await Attempt("replacement-invocation-join", async () => { _ = await Outcome(_replacementInvocation, "replacement-invocation-join"); });
                if (!_replacementInvocation.IsCompleted) return;
            }
            if (_replacementReturned is not null)
            {
                // Unexpected returned source is still a real owner, never a
                // synthetic substitute for the expected synchronous API throw.
                await Attempt("replacement-returned-join", async () => { _ = await Outcome(_replacementReturned, "replacement-returned-join"); });
                if (!_replacementReturned.IsCompleted) return;
            }
            Check("hook-restore", () =>
            {
                if (_hookWriteAttempted)
                {
                    Require(_hookSnapshot && _hookField is not null, "exact hook prior snapshot");
                    _hookField!.SetValue(null, _previousHook);
                    Require(ReferenceEquals(_hookField.GetValue(null), _previousHook), "exact hook restoration");
                }
                _hookRestored = true;
            });
            lock (_gate) foreach (var failure in _hookFailures) _failures.Add(("constructor-hook", failure));
            if (_registrationAttempted) Check("capture-retained-owner", () => { CaptureModule(); _retainedWave1 = CaptureWave(_retainedModule!); });
            if (_replacementOldRegistrationAttempted) Check("capture-old-owner", () =>
            { CaptureReplacementOldModule(); _oldWave1 = CaptureWave(_replacementOldModule!); });
            if (_endpoint is not null)
            {
                await Attempt("stop", () => InvokeHost(false));
                if (_stopInvocation is { IsCompleted: false } || _stop is { IsCompleted: false }) return;
                await Attempt("dispose", () => InvokeHost(true));
                if (_disposeInvocation is { IsCompleted: false } || _dispose is { IsCompleted: false }) return;
            }
            else if (_offered) return; // Build transfer unknown: retain builder/transport
            else await Attempt("unoffered-transport", () => Success(_transport.DisposeAsync().AsTask(), "unoffered-transport"));
            if (_retainedModule is not null)
            {
                Check("late-retained-wave", () => _retainedWave2 = CaptureWave(_retainedModule));
                if (_retainedWave1 is not null && (_retainedWave2 is null || !_retainedWave1.SameTasksAs(_retainedWave2))) await JoinWave(_retainedWave1, false, true);
                if (_retainedWave2 is not null) await JoinWave(_retainedWave2, true, true);
            }
            if (_replacementOldModule is not null)
            {
                Check("late-old-wave", () => _oldWave2 = CaptureWave(_replacementOldModule));
                if (_oldWave1 is not null && (_oldWave2 is null || !_oldWave1.SameTasksAs(_oldWave2))) await JoinWave(_oldWave1, false, false);
                if (_oldWave2 is not null) await JoinWave(_oldWave2, true, false);
            }
            await Attempt("server-tails", ServerTails);
            Check("physical-exit", () =>
            {
                Require(_oracleComplete && _hookRestored && _retainedModule is not null && _retainedCodecOwner is not null && _retainedWave2 is not null &&
                    _replacementOldModule is not null && _replacementOldCodecOwner is not null && _replacementOldManifest is not null && _oldWave2 is not null,
                    "complete known replacement and two-module cleanup graph");
                var old = Receipts(_retainedManifest!); var incoming = Receipts(_incomingManifest!);
                Require(old.Length == 1 && old[0].Count == 1 && old[0].Order == 1 && ReferenceEquals(old[0].Failure, _retainedFailure), "old scope actual one cleanup");
                Require(incoming.Length == 1 && incoming[0].Count == 1 && incoming[0].Order == 1 && ReferenceEquals(incoming[0].Failure, _incomingFailure), "incoming rollback scope not redisposed");
                Require(_replacementOldManifest!.Codecs.Count == 0 && Receipts(_replacementOldManifest).Length == 0,
                    "old codec-free module never created a scope");
                _replacementOldModule!.AssertAccountingInvariant();
                Require(_replacementOldModule.State == SharpLinkDynamicModuleState.Released &&
                    _replacementOldModule.RemainingCalls == 0 && _replacementOldModule.RemainingStreams == 0,
                    "codec-free module physical/accounting exit");
                _retainedModule!.AssertAccountingInvariant();
                Require(_retainedModule.State == SharpLinkDynamicModuleState.Released && _retainedModule.RemainingCalls == 0 && _retainedModule.RemainingStreams == 0, "module physical/accounting exit");
                WithRegistry(() =>
                {
                    Require(_serverRegistry is not null ? !_serverRegistry.DynamicModules.ContainsKey(_retainedAssembly) && !_serverRegistry.UnregisterOperations.ContainsKey(_retainedAssembly)
                        : !_clientRegistry!.DynamicModules.ContainsKey(_retainedAssembly) && !ClientOperations().ContainsKey(_retainedAssembly), "registry/retirement ownership removed");
                    Require(_serverRegistry is not null
                        ? !_serverRegistry.DynamicModules.ContainsKey(_replacementOldAssembly!) && !_serverRegistry.UnregisterOperations.ContainsKey(_replacementOldAssembly!)
                        : !_clientRegistry!.DynamicModules.ContainsKey(_replacementOldAssembly!) && !ClientOperations().ContainsKey(_replacementOldAssembly!),
                        "codec-free registry/retirement ownership removed");
                    Require(_retainedWave2!.SameAs(_retainedModule) && _oldWave2!.SameAs(_replacementOldModule), "neither module has an uncaptured retirement owner");
                });
                Require(Field<int>(_runtime!, "_disposed") != 0 && ReadField(_runtime!, "_metadataOwner") is null &&
                    ReadField(_runtime!, "_publicationMetadataOwner") is null && ReadField(_runtime!, "_generatedManifests") is null, "actual Runtime metadata exit");
                Require(_retainedCodecOwner!.Identity.IsClosed && ReadField(_retainedCodecOwner!, "_metadata") is null && ReadField(_retainedCodecOwner!, "_baseProvider") is null, "actual old codec owner exit");
                Require(_replacementOldCodecOwner!.Identity.IsClosed && ReadField(_replacementOldCodecOwner!, "_metadata") is null &&
                    ReadField(_replacementOldCodecOwner!, "_baseProvider") is null, "actual codec-free registration owner exit");
                Require(_transport.Disposals.Count == 1 && _transport.Disposals[0].IsCompletedSuccessfully, "actual stateless transport disposed once");
                if (_client is not null) Require(_client.State == SharpLinkConnectionState.Stopped, "Client stops after reporting exact scope error");
                Require(_stop is not null && ReferenceEquals(_stop, _dispose), "Dispose observes original Stop task");
            });
            lock (_gate)
            {
                foreach (var pair in _outcomes)
                    if (!pair.Value.IsCompletedSuccessfully || !_accepted.Contains(pair.Key))
                    {
                        _failures.Add(("unaccepted-source", new InvalidOperationException("Unknown or pending B3c original source retained")));
                        if (pair.Value.IsCompletedSuccessfully)
                        {
                            var outcome = pair.Value.Result;
                            if (outcome.FullTaskFailure is { } full) _failures.Add(("unaccepted-source-full-graph", full));
                            else if (outcome.AwaitFailure is { } raw) _failures.Add(("unaccepted-source-cancellation", raw));
                        }
                    }
                if (_failures.Count != 0) return;
            }
            // All actual callback/registration/host/physical sources ended. An
            // unload REQUEST here does not claim immediate collectible reclamation.
            Check("incoming-unload", () => _context?.Unload());
            Check("old-unload", () => _replacementOldContext?.Unload());
            lock (_gate) if (_failures.Count != 0) return;
            Require(_snapshots, "environment snapshots precede restore");
            for (var i = 0; i < EnvironmentKeys.Length; i++)
            {
                var index = i;
                if (_writeAttempted[index]) Check("environment-restore", () => Environment.SetEnvironmentVariable(EnvironmentKeys[index], _previousEnvironment[index]));
            }
            lock (_gate) if (_failures.Count != 0) return;
            try { RollbackState.TestIsolation.Release(); _permit = false; }
            catch (Exception failure) { Fail("permit-release", failure); return; }
            lock (RootsGate) Roots.Remove(this);
        }
    }

    private sealed class B3bOwner(bool server)
    {
        private static readonly Lock RootsGate = new();
        private static readonly HashSet<B3bOwner> Roots = [];
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);
        private static readonly string[] EnvironmentKeys = ["SHARPLINK_ROLLBACK_SCHEMA", "SHARPLINK_ROLLBACK_CODEC_IDENTITY",
            "SHARPLINK_ROLLBACK_DISABLE_CODEC", "SHARPLINK_ROLLBACK_RETIREMENT_CONTRACT"];
        private readonly string?[] _previousEnvironment = new string?[EnvironmentKeys.Length];
        private readonly bool[] _writeAttempted = new bool[EnvironmentKeys.Length];
        private readonly Lock _gate = new();
        private readonly Dictionary<Task, Task<B3bOutcome>> _outcomes = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<Task> _accepted = new(ReferenceEqualityComparer.Instance);
        private readonly List<(string Stage, Exception Failure)> _failures = [];
        private readonly List<(string Stage, Task Original)> _taskEdges = [];
        private readonly List<Exception> _hookFailures = [];
        private readonly List<ISharpLinkGeneratedAssemblyManifest> _observedManifests = [];
        private readonly B3bTransport _transport = new();
        private bool _permit, _snapshots, _offered, _registrationAttempted, _hookSnapshot, _hookWriteAttempted;
        private object? _builder;
        private IAsyncDisposable? _endpoint;
        private SharpLinkClient? _client;
        private SharpLinkServer? _server;
        private SharpLinkRuntimeContext? _runtime;
        private ClientAssemblyRegistry? _clientRegistry;
        private ServerServiceModuleRegistry? _serverRegistry;
        private SharpLinkDynamicModule? _module;
        private RpcGeneratedManifestRegistration? _codecOwner;
        private ISharpLinkGeneratedAssemblyManifest? _oldManifest, _incomingManifest;
        private Exception? _oldFailure, _incomingFailure;
        private Task? _setup, _registration, _stopInvocation, _stop, _disposeInvocation, _dispose, _terminal;
        private B3aWave? _wave1, _wave2;
        private PluginLoadContext? _context;
        private Assembly? _incomingAssembly;
        private FieldInfo? _hookField;
        private object? _previousHook, _defaultObserver, _defaultStarted, _defaultRelease;
        private Action<ISharpLinkGeneratedAssemblyManifest>? _installedHook;
        private bool _hookRestored, _oracleComplete;
        private readonly Assembly _oldAssembly = typeof(RollbackMarker).Assembly;

        internal static async Task Run(bool server, CancellationToken token)
        {
            var owner = new B3bOwner(server);
            lock (RootsGate) Roots.Add(owner);
            try
            {
                await RollbackState.TestIsolation.WaitAsync(token);
                owner._permit = true;
                try
                {
                    owner.PrepareEnvironment();
                    owner._setup = owner.Own(Task.Run(owner.Initialize), "setup");
                    await owner.Success(owner._setup, "setup");
                    owner._registration = owner.Own(Task.Run(owner.RegisterIncoming), "registration");
                    await owner.CheckRollback();
                }
                catch (Exception failure) { owner.Fail("body", failure); }
                finally
                {
                    try { await owner.Close(); }
                    catch (Exception failure) { owner.Fail("close", failure); }
                }
            }
            catch (Exception failure) { owner.Fail("admission", failure); }
            finally
            {
                if (!owner._permit && owner._endpoint is null && !owner._offered)
                    lock (RootsGate) Roots.Remove(owner);
            }
            Exception[] failures;
            lock (owner._gate) failures = owner._failures.ConvertAll(x => x.Failure).ToArray();
            if (failures.Length != 0) throw new AggregateException(failures);
        }

        private void Fail(string stage, Exception failure) { lock (_gate) _failures.Add((stage, failure)); }
        private void Require(bool condition, string message) => Ensure(condition, "B3b " + message);
        private static object? ReadField(object owner, string name)
            => (owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing B3b field " + name)).GetValue(owner);
        private static T Field<T>(object owner, string name) => (T)(ReadField(owner, name)
            ?? throw new InvalidOperationException("Missing B3b owner " + name));

        private void PrepareEnvironment()
        {
            // Read every prior value before any mutation. Foreign default hooks
            // are not invoked, cleared or chained by this fixture.
            for (var i = 0; i < EnvironmentKeys.Length; i++)
                _previousEnvironment[i] = Environment.GetEnvironmentVariable(EnvironmentKeys[i]);
            _snapshots = true;
            _defaultObserver = RollbackState.ManifestConstructed;
            _defaultStarted = RollbackState.ManifestConstructionStarted;
            _defaultRelease = RollbackState.ManifestConstructionRelease;
            Require(_defaultObserver is null && _defaultStarted is null && _defaultRelease is null,
                "default assembly has no foreign observer or construction barrier");
            for (var i = 0; i < EnvironmentKeys.Length; i++)
            {
                _writeAttempted[i] = true;
                Environment.SetEnvironmentVariable(EnvironmentKeys[i], i == 0 ? "first-schema" : null);
            }
        }

        private void Initialize()
        {
            if (server)
            {
                var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
                _builder = builder; _offered = true; builder.UseTransport(_transport);
                var endpoint = builder.Build(); _endpoint = endpoint; _server = (SharpLinkServer)endpoint;
                _runtime = Field<SharpLinkRuntimeContext>(_server, "_runtimeContext");
                _serverRegistry = Field<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry");
                _registrationAttempted = true;
                Require(endpoint.RegisterAssembly(_oldAssembly).Succeeded, "first Server registration");
            }
            else
            {
                var builder = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).DisableRequestTimeout();
                _builder = builder; _offered = true; builder.UseTransport(_transport);
                var endpoint = builder.Build(); _endpoint = endpoint; _client = (SharpLinkClient)endpoint;
                _runtime = (SharpLinkRuntimeContext)_client.RuntimeContext;
                _clientRegistry = Field<ClientAssemblyRegistry>(_client, "_assemblyRegistry");
                _registrationAttempted = true;
                Require(endpoint.RegisterAssembly(_oldAssembly).Succeeded, "first Client registration");
            }
            CaptureModule();
            _codecOwner = _module!.CodecRegistration;
            _oldManifest = ((PreparedGeneratedManifest)_module!.Manifest).OriginalManifest;
            Require(_oldManifest is RollbackManifest && ReferenceEquals(_oldManifest.OwnerAssembly, _oldAssembly), "exact first instance");
            var oldReceipt = Receipts(_oldManifest);
            Require(oldReceipt.Length == 1 && oldReceipt[0].Count == 0 && oldReceipt[0].Order == 0, "first scope not yet disposed");
            _oldFailure = oldReceipt[0].Failure;
            _context = new PluginLoadContext(server ? "b3b-server-registration" : "b3b-client-registration");
            _incomingAssembly = _context.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location);
            var state = _incomingAssembly.GetType(typeof(RollbackState).FullName!, throwOnError: true)!;
            Require(state.GetField(nameof(RollbackState.ManifestConstructionStarted))!.GetValue(null) is null &&
                state.GetField(nameof(RollbackState.ManifestConstructionRelease))!.GetValue(null) is null, "incoming has no foreign barrier");
            _hookField = state.GetField(nameof(RollbackState.ManifestConstructed))!;
            _previousHook = _hookField.GetValue(null); _hookSnapshot = true;
            _installedHook = ObserveIncoming;
            _hookWriteAttempted = true;
            _hookField.SetValue(null, _installedHook);
            _writeAttempted[0] = true;
            Environment.SetEnvironmentVariable(EnvironmentKeys[0], "second-schema");
        }

        private void ObserveIncoming(ISharpLinkGeneratedAssemblyManifest manifest)
        {
            // Root actual this BEFORE validating. A callback throw/constructor
            // non-return cannot discard the only instance receipt owner.
            lock (_gate)
            {
                var previous = _incomingManifest;
                _observedManifests.Add(manifest);
                _incomingManifest ??= manifest;
                try
                {
                    Require(previous is null, "one actual incoming construction");
                    Require(ReferenceEquals(manifest.OwnerAssembly, _incomingAssembly), "incoming observer exact assembly");
                }
                catch (Exception failure) { _hookFailures.Add(failure); throw; }
            }
        }

        private SharpLinkAssemblyRegistrationResult RegisterIncoming()
            => server ? _server!.RegisterAssembly(_incomingAssembly!) : _client!.RegisterAssembly(_incomingAssembly!);

        private static (Exception Failure, int Count, int Order)[] Receipts(ISharpLinkGeneratedAssemblyManifest manifest)
            => ((Exception Failure, int Count, int Order)[])(manifest.GetType()
                .GetMethod(nameof(RollbackManifest.GetScopeCleanupReceipts), BindingFlags.Instance | BindingFlags.Public)!
                .Invoke(manifest, null) ?? throw new InvalidOperationException("Missing actual scope receipt"));

        private void WithRegistry(Action action)
        {
            if (_serverRegistry is not null) { lock (_serverRegistry.Gate) action(); }
            else if (_clientRegistry is not null) { lock (Field<Lock>(_clientRegistry, "_gate")) action(); }
            else throw new InvalidOperationException("Unknown B3b registry");
        }

        private void CaptureModule()
        {
            if (_module is not null) return;
            WithRegistry(() =>
            {
                var found = _serverRegistry is not null ? _serverRegistry.DynamicModules.TryGetValue(_oldAssembly, out _module)
                    : _clientRegistry!.DynamicModules.TryGetValue(_oldAssembly, out _module);
                Require(found && _module is not null, "exact first module root");
            });
        }

        private sealed record B3bOutcome(Exception? AwaitFailure, AggregateException? FullTaskFailure)
        {
            internal bool Succeeded => AwaitFailure is null && FullTaskFailure is null;
        }
        private static async Task<B3bOutcome> Observe(Task source)
        {
            try { await source.ConfigureAwait(false); return new(null, null); }
            catch (Exception failure) { return new(failure, source.Exception); }
        }
        private T Own<T>(T source, string stage) where T : Task
        {
            lock (_gate)
            {
                _taskEdges.Add((stage, source));
                if (!_outcomes.ContainsKey(source)) _outcomes.Add(source, Observe(source));
            }
            return source;
        }
        private async Task<B3bOutcome> Outcome(Task source, string stage)
        {
            _ = Own(source, stage);
            Task<B3bOutcome> observer;
            lock (_gate) observer = _outcomes[source];
            return await observer.WaitAsync(Bound); // diagnostic error is NOT source error data
        }
        private void Accept(Task source) { lock (_gate) _accepted.Add(source); }
        private async Task Success(Task source, string stage)
        {
            var outcome = await Outcome(source, stage);
            if (!outcome.Succeeded)
                throw new AggregateException("Unexpected B3b source failure: " + stage,
                    outcome.FullTaskFailure ?? outcome.AwaitFailure!);
            Accept(source);
        }
        private static bool SingleTaskFault(B3bOutcome outcome)
            => outcome.AwaitFailure is not null && outcome.FullTaskFailure is { InnerExceptions.Count: 1 } &&
                ReferenceEquals(outcome.FullTaskFailure.InnerExceptions[0], outcome.AwaitFailure);

        private async Task CheckRollback()
        {
            var outcome = await Outcome(_registration!, "registration-oracle");
            Require(_incomingManifest is not null && _oldManifest is not null, "both actual manifest instances");
            var incoming = Receipts(_incomingManifest!); var old = Receipts(_oldManifest!);
            Require(incoming.Length == 1 && incoming[0].Count == 1 && incoming[0].Order == 1, "incoming scope rollback exactly once");
            _incomingFailure = incoming[0].Failure;
            Require(old.Length == 1 && old[0].Count == 0 && old[0].Order == 0 && ReferenceEquals(old[0].Failure, _oldFailure), "original scope unchanged");
            var firstCodecs = _oldManifest!.Codecs; var incomingCodecs = _incomingManifest!.Codecs;
            Require(firstCodecs.Count == 1 && incomingCodecs.Count == 1 && firstCodecs[0].TargetType == typeof(string) &&
                incomingCodecs[0].TargetType == typeof(string) && !firstCodecs[0].CodecHash.IsEmpty &&
                !incomingCodecs[0].CodecHash.IsEmpty && firstCodecs[0].CodecHash != incomingCodecs[0].CodecHash, "actual distinct string codec identities");
            var expected = $"CodecConflict: Codec conflict for '{typeof(string).FullName}': existing CodecHash '{firstCodecs[0].CodecHash}', incoming CodecHash '{incomingCodecs[0].CodecHash}'.";
            Require(SingleTaskFault(outcome) && outcome.AwaitFailure is AggregateException { InnerExceptions.Count: 2 }, "exact two-part registration failure");
            var failure = (AggregateException)outcome.AwaitFailure!;
            Require(failure.InnerExceptions[0].GetType() == typeof(InvalidOperationException) && failure.InnerExceptions[0].InnerException is null &&
                failure.InnerExceptions[0].Message == expected && ReferenceEquals(failure.InnerExceptions[1], _incomingFailure),
                "structured exact conflict then original incoming adapter failure, without unknown siblings");
            Require(!ReferenceEquals(_oldFailure, _incomingFailure), "different instances own different original failures");
            WithRegistry(() =>
            {
                Require(_module!.State == SharpLinkDynamicModuleState.Running && ReferenceEquals(_module.CodecRegistration, _codecOwner), "first module remains Running and identical");
                Require(_serverRegistry is not null ? !_serverRegistry.DynamicModules.ContainsKey(_incomingAssembly!) &&
                    !_serverRegistry.UnregisterOperations.ContainsKey(_incomingAssembly!) :
                    !_clientRegistry!.DynamicModules.ContainsKey(_incomingAssembly!) && !ClientOperations().ContainsKey(_incomingAssembly!),
                    "failed incoming assembly has no published or retiring module");
            });
            lock (_gate) Require(_hookFailures.Count == 0, "constructor observer had no failure");
            Accept(_registration!);
            _oracleComplete = true;
        }

        private Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)> ClientOperations()
            => Field<Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)>>(_clientRegistry!, "_unregisterOperations");

        private async Task Attempt(string stage, Func<Task> action)
        {
            try { await action(); } catch (Exception failure) { Fail(stage, failure); }
        }
        private void Check(string stage, Action action)
        {
            try { action(); } catch (Exception failure) { Fail(stage, failure); }
        }
        private async Task KnownFault(Task task, string stage, Func<Exception, bool> matches)
        {
            var outcome = await Outcome(task, stage);
            if (!SingleTaskFault(outcome) || !matches(outcome.AwaitFailure!))
            {
                var rejected = new InvalidOperationException("B3b exact known fault mismatch at " + stage);
                if (outcome.FullTaskFailure is { } full) throw new AggregateException(rejected, full);
                if (outcome.AwaitFailure is { } raw) throw new AggregateException(rejected, raw);
                throw rejected;
            }
            Accept(task);
        }
        private void Data(string stage, Exception? failure)
        {
            if (failure is not null) Fail(stage, failure);
        }

        private bool IsOldPhysical(Exception error)
            => error is PhysicalTaskFailureException physical && _oldFailure is not null && ReferenceEquals(physical.RawException, _oldFailure);

        private B3aWave CaptureWave()
        {
            B3aWave? wave = null;
            WithRegistry(() => wave = new B3aWave(_module!));
            if (wave!.Control is not null) _ = Own(wave.Control.Driver.Task, "driver");
            if (wave.Physical is not null) _ = Own(wave.Physical, "physical");
            if (wave.Tracking is not null) _ = Own(wave.Tracking, "tracking");
            if (wave.Observer is not null) _ = Own(wave.Observer, "retirement-observer");
            if (wave.Cleanup is not null) _ = Own(wave.Cleanup, "module-cleanup");
            return wave;
        }

        private async Task InvokeHost(bool dispose)
        {
            var invocation = Task.Factory.StartNew(() =>
            {
                var source = dispose ? _endpoint!.DisposeAsync().AsTask() : _endpoint is ISharpLinkServer host
                    ? host.StopAsync(TimeSpan.Zero).AsTask() : ((ISharpLinkClient)_endpoint!).StopAsync().AsTask();
                _ = Own(source, dispose ? "dispose" : "stop");
                if (dispose) _dispose = source; else _stop = source;
                return source;
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            if (dispose) _disposeInvocation = Own(invocation, "dispose-invocation");
            else _stopInvocation = Own(invocation, "stop-invocation");
            await Success(invocation, "host-invocation");
            var original = await invocation;
            if (_oldFailure is null) await Success(original, "unregistered-host-cleanup");
            else await KnownFault(original, dispose ? "dispose" : "stop", server ? IsOldPhysical : e => ReferenceEquals(e, _oldFailure));
        }

        private async Task JoinWave(B3aWave wave, bool final)
        {
            if (wave.Control is null)
            {
                Check("wave", () => Require(!final && wave.Physical is null && wave.Tracking is null && wave.Observer is null, "no missing final control"));
                return;
            }
            Check("wave-shape", () => Require(wave.Control.Origin == RetirementOrigin.Stop && wave.Physical is not null &&
                wave.Tracking is not null && wave.Observer is not null, "complete Stop-origin owner"));
            await Attempt("driver", async () =>
            {
                await Success(wave.Control.Driver.Task, "driver"); var value = wave.Control.Driver.Task.Result;
                Data("driver-primary", value.PrimaryFailure); Data("driver-cancellation", value.CancellationFailure);
                Data("driver-timer", value.TimerFailure); Data("driver-wake", value.WakeFailure);
                Require(!value.Aborted && !value.HasRetainedTimer, "driver outcome success and real timer exit");
            });
            if (wave.Physical is not null) await Attempt("physical", () => KnownFault(wave.Physical, "physical", IsOldPhysical));
            if (wave.Tracking is not null) await Attempt("tracking", async () =>
            {
                await Success(wave.Tracking, "tracking"); Data("tracking-data", wave.Tracking.Result);
            });
            if (wave.Observer is not null) await Attempt("retirement-observer", async () =>
            {
                await Success(wave.Observer, "retirement-observer"); var value = wave.Observer.Result;
                Data("observer-refund-data", value.Failure);
                Require(!value.SupervisorOwned && wave.Receipt is null, "observer refund success/Stop ownership");
            });
            if (final) Check("cleanup-shape", () => Require(wave.Cleanup is not null, "actual cleanup outcome source"));
            if (wave.Cleanup is not null) await Attempt("module-cleanup", async () =>
            {
                await Success(wave.Cleanup, "module-cleanup");
                if (!ReferenceEquals(wave.Cleanup.Result, _oldFailure))
                {
                    Data("unexpected-module-cleanup-data", wave.Cleanup.Result);
                    Require(false, "cleanup data exact original old scope failure");
                }
            });
        }

        private async Task ServerTails()
        {
            if (_server is null) return;
            var lifecycle = Field<SharpLinkServer.ServerLifecycleCoordinator>(_server, "_lifecycle");
            _terminal = Own(lifecycle.WaitForShutdownAsync(CancellationToken.None), "terminal-completion");
            var service = lifecycle.DeferredServiceCleanupTaskForDiagnostics;
            var serviceObserver = (Task?)ReadField(lifecycle, "_serviceCleanupObserver");
            var shutdownObserver = lifecycle.ShutdownCleanupObserverTaskForDiagnostics;
            var terminalObserver = lifecycle.TerminalFailureObserverTaskForDiagnostics;
            var strictObserver = lifecycle.StrictShutdownCleanupObserverForDiagnostics;
            var strict = lifecycle.StrictPhysicalDrainForDiagnostics;
            foreach (var source in new[] { service, serviceObserver, shutdownObserver, terminalObserver, strictObserver, strict.Drain })
                if (source is not null) _ = Own(source, "server-tail");
            if (service is not null) await Attempt("service-cleanup", () => KnownFault(service, "service-cleanup", IsOldPhysical));
            else Check("service-cleanup", () => Require(_module is null, "normal registered host has actual service cleanup"));
            foreach (var source in new[] { serviceObserver, shutdownObserver, terminalObserver, strictObserver, strict.Drain })
                if (source is not null) await Attempt("server-observer", () => Success(source, "server-observer"));
            await Attempt("terminal-completion", () => KnownFault(_terminal!, "terminal-completion", error =>
                IsOldPhysical(error) && _stop is { IsFaulted: true } && ReferenceEquals(error, _stop.Exception!.InnerExceptions[0])));
            var final = lifecycle.StrictPhysicalDrainForDiagnostics;
            Data("strict-drain-data", final.Failure);
            var terminalFailure = lifecycle.StrictPhysicalTerminalFailureForDiagnostics;
            var terminalMatches = _stop is { IsFaulted: true } &&
                ReferenceEquals(terminalFailure, _stop.Exception!.InnerExceptions[0]);
            if (!terminalMatches) Data("unexpected-terminal-data", terminalFailure);
            Check("server-late-tail", () => Require(ReferenceEquals(service, lifecycle.DeferredServiceCleanupTaskForDiagnostics) &&
                ReferenceEquals(serviceObserver, ReadField(lifecycle, "_serviceCleanupObserver")) &&
                ReferenceEquals(shutdownObserver, lifecycle.ShutdownCleanupObserverTaskForDiagnostics) &&
                ReferenceEquals(terminalObserver, lifecycle.TerminalFailureObserverTaskForDiagnostics) &&
                ReferenceEquals(strictObserver, lifecycle.StrictShutdownCleanupObserverForDiagnostics) && ReferenceEquals(strict.Drain, final.Drain),
                "no late uncaptured server tail"));
            Check("service-observer-path", () => Require(serviceObserver is null, "normal Created cleanup did not defer its service observer"));
            Check("strict-exit", () => Require(!final.Requested || final.Observed, "strict drain outcome"));
            Check("terminal-data", () => Require(terminalMatches, "actual terminal failure equals actual Stop wrapper"));
            Check("service-stop-identity", () => Require(service is { IsFaulted: true } && _stop is { IsFaulted: true } &&
                ReferenceEquals(service.Exception!.InnerExceptions[0], _stop.Exception!.InnerExceptions[0]),
                "Stop preserves exact service-cleanup sealed wrapper"));
            Check("server-terminal-state", () => Require(lifecycle.LifecycleState == SharpLinkServerLifecycleState.Faulted,
                "intentional old scope cleanup leaves Server Faulted"));
        }

        private async Task Close()
        {
            GC.KeepAlive(_builder);
            if (_setup is not null) await Attempt("setup-join", () => Success(_setup, "setup-join"));
            if (_setup is { IsCompleted: false }) return; // may still publish host/hook
            if (_registration is not null)
            {
                // Keep complete outcome even when the business oracle did not
                // accept it. Never reclassify an arbitrary caught container.
                await Attempt("registration-join", async () => { _ = await Outcome(_registration, "registration-join"); });
                if (!_registration.IsCompleted) return;
            }
            Check("hook-restore", () =>
            {
                if (_hookWriteAttempted)
                {
                    Require(_hookSnapshot && _hookField is not null, "exact hook prior snapshot");
                    _hookField!.SetValue(null, _previousHook);
                    Require(ReferenceEquals(_hookField.GetValue(null), _previousHook), "exact hook restoration");
                }
                _hookRestored = true;
            });
            lock (_gate) foreach (var failure in _hookFailures) _failures.Add(("constructor-hook", failure));
            if (_registrationAttempted) Check("capture-first-owner", () => { CaptureModule(); _wave1 = CaptureWave(); });
            if (_endpoint is not null)
            {
                await Attempt("stop", () => InvokeHost(false));
                if (_stopInvocation is { IsCompleted: false } || _stop is { IsCompleted: false }) return;
                await Attempt("dispose", () => InvokeHost(true));
                if (_disposeInvocation is { IsCompleted: false } || _dispose is { IsCompleted: false }) return;
            }
            else if (_offered) return; // Build transfer unknown: retain builder/transport
            else await Attempt("unoffered-transport", () => Success(_transport.DisposeAsync().AsTask(), "unoffered-transport"));
            if (_module is not null)
            {
                Check("late-wave", () => _wave2 = CaptureWave());
                if (_wave1 is not null && (_wave2 is null || !_wave1.SameTasksAs(_wave2))) await JoinWave(_wave1, false);
                if (_wave2 is not null) await JoinWave(_wave2, true);
            }
            await Attempt("server-tails", ServerTails);
            Check("physical-exit", () =>
            {
                Require(_oracleComplete && _hookRestored && _module is not null && _codecOwner is not null && _wave2 is not null,
                    "complete known registration and cleanup graph");
                var old = Receipts(_oldManifest!); var incoming = Receipts(_incomingManifest!);
                Require(old.Length == 1 && old[0].Count == 1 && old[0].Order == 1 && ReferenceEquals(old[0].Failure, _oldFailure), "old scope actual one cleanup");
                Require(incoming.Length == 1 && incoming[0].Count == 1 && incoming[0].Order == 1 && ReferenceEquals(incoming[0].Failure, _incomingFailure), "incoming rollback scope not redisposed");
                _module!.AssertAccountingInvariant();
                Require(_module.State == SharpLinkDynamicModuleState.Released && _module.RemainingCalls == 0 && _module.RemainingStreams == 0, "module physical/accounting exit");
                WithRegistry(() =>
                {
                    Require(_serverRegistry is not null ? !_serverRegistry.DynamicModules.ContainsKey(_oldAssembly) && !_serverRegistry.UnregisterOperations.ContainsKey(_oldAssembly)
                        : !_clientRegistry!.DynamicModules.ContainsKey(_oldAssembly) && !ClientOperations().ContainsKey(_oldAssembly), "registry/retirement ownership removed");
                    Require(_wave2!.SameAs(_module), "no uncaptured retirement owner");
                });
                Require(Field<int>(_runtime!, "_disposed") != 0 && ReadField(_runtime!, "_metadataOwner") is null &&
                    ReadField(_runtime!, "_publicationMetadataOwner") is null && ReadField(_runtime!, "_generatedManifests") is null, "actual Runtime metadata exit");
                Require(_codecOwner!.Identity.IsClosed && ReadField(_codecOwner!, "_metadata") is null && ReadField(_codecOwner!, "_baseProvider") is null, "actual old codec owner exit");
                Require(_transport.Disposals.Count == 1 && _transport.Disposals[0].IsCompletedSuccessfully, "actual stateless transport disposed once");
                if (_client is not null) Require(_client.State == SharpLinkConnectionState.Stopped, "Client stops after reporting exact scope error");
                Require(_stop is not null && ReferenceEquals(_stop, _dispose), "Dispose observes original Stop task");
            });
            lock (_gate)
            {
                foreach (var pair in _outcomes)
                    if (!pair.Value.IsCompletedSuccessfully || !_accepted.Contains(pair.Key))
                    {
                        _failures.Add(("unaccepted-source", new InvalidOperationException("Unknown or pending B3b original source retained")));
                        if (pair.Value.IsCompletedSuccessfully)
                        {
                            var outcome = pair.Value.Result;
                            if (outcome.FullTaskFailure is { } full) _failures.Add(("unaccepted-source-full-graph", full));
                            else if (outcome.AwaitFailure is { } raw) _failures.Add(("unaccepted-source-cancellation", raw));
                        }
                    }
                if (_failures.Count != 0) return;
            }
            // All actual callback/registration/host/physical sources ended. An
            // unload REQUEST here does not claim immediate collectible reclamation.
            Check("unload", () => _context?.Unload());
            lock (_gate) if (_failures.Count != 0) return;
            Require(_snapshots, "environment snapshots precede restore");
            for (var i = 0; i < EnvironmentKeys.Length; i++)
            {
                var index = i;
                if (_writeAttempted[index]) Check("environment-restore", () => Environment.SetEnvironmentVariable(EnvironmentKeys[index], _previousEnvironment[index]));
            }
            lock (_gate) if (_failures.Count != 0) return;
            try { RollbackState.TestIsolation.Release(); _permit = false; }
            catch (Exception failure) { Fail("permit-release", failure); return; }
            lock (RootsGate) Roots.Remove(this);
        }
    }

    private sealed class B3bTransport : IClientTransportFactory, IServerTransportListener
    {
        internal List<Task> Disposals { get; } = [];
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()
        {
            var source = ValueTask.CompletedTask.AsTask(); Disposals.Add(source); return new ValueTask(source);
        }
    }

    // Only the three B3a controls use this owner. Its static root precedes every
    // resource acquisition and survives unknown setup, physical or restoration exit.
    private sealed class B3aOwner(bool server, bool manual)
    {
        private static readonly Lock RootsGate = new();
        private static readonly HashSet<B3aOwner> Roots = [];
        private static readonly TimeSpan Bound = TimeSpan.FromSeconds(2);
        private readonly Lock _tasksGate = new();
        private readonly Dictionary<Task, Task<SourceOutcome>> _outcomes = new(ReferenceEqualityComparer.Instance);
        private readonly List<Exception> _failures = [];
        private readonly HashSet<Task> _reportedSources = new(ReferenceEqualityComparer.Instance);
        private readonly List<(Task Source, Exception Failure)> _sourceObservations = [];
        private readonly HashSet<(object Owner, string Stage)> _reportedData = [];
        private readonly List<(object Owner, string Stage, Exception Failure)> _dataObservations = [];
        private bool _ownershipFailed;
        private readonly Assembly _assembly = typeof(RollbackMarker).Assembly;
        private readonly B3aTransport _transport = new();
        private readonly B3aClock? _clock = manual ? new B3aClock() : null;
        private readonly ManualTimeProvider? _unrelated = manual ? new ManualTimeProvider() : null;
        private bool _permit, _snapshot, _writeAttempted, _offered, _buildAttempted, _registrationAttempted;
        private string? _previous;
        private object? _builder;
        private IAsyncDisposable? _endpoint;
        private SharpLinkClient? _client;
        private SharpLinkServer? _server;
        private SharpLinkRuntimeContext? _runtime;
        private ClientAssemblyRegistry? _clientRegistry;
        private ServerServiceModuleRegistry? _serverRegistry;
        private SharpLinkDynamicModule? _module;
        private Task? _setup, _unregisterInvocation, _stopInvocation, _disposeInvocation;
        private Task<SharpLinkAssemblyUnregisterResult>? _unregisterSource;
        private Task? _stopSource, _disposeSource, _ownerDrain, _unrelatedDrain;
        private B3aWave? _wave1, _wave2;
        private SharpLinkDynamicModuleLease _lease;
        private CancellationTokenRegistration _callback;
        private bool _callbackInstalled;
        private int _cancellations;
        private readonly TaskCompletionSource _cancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _entry;

        internal static async Task Run(bool server, bool manual, CancellationToken token, Func<B3aOwner, Task> body)
        {
            var owner = new B3aOwner(server, manual);
            lock (RootsGate) Roots.Add(owner);
            try
            {
                await RollbackState.TestIsolation.WaitAsync(token);
                owner._permit = true;
                try
                {
                    owner._previous = Environment.GetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC");
                    owner._snapshot = true;
                    owner._writeAttempted = true;
                    Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
                    owner._setup = owner.Own(Task.Run(owner.Initialize));
                    await owner.Join(owner._setup);
                    await body(owner);
                }
                catch (Exception failure) { owner.Record(failure); }
                finally
                {
                    try { await owner.Close(); }
                    catch (Exception failure) { owner.Record(failure); }
                }
            }
            catch (Exception failure) { owner.Record(failure); }
            finally
            {
                // Admission failure acquired neither permit nor host. All later
                // paths remove the root only after positive Close certificates.
                if (!owner._permit && !owner._buildAttempted)
                    lock (RootsGate) Roots.Remove(owner);
            }
            if (owner._failures.Count != 0) throw new AggregateException(owner._failures);
        }

        private void Initialize()
        {
            if (server)
            {
                var builder = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty);
                _builder = builder;
                if (_clock is not null) builder.UseTimeProvider(_clock);
                _offered = true;
                builder.UseTransport(_transport);
                _buildAttempted = true;
                var endpoint = builder.Build();
                _endpoint = endpoint;
                _server = (SharpLinkServer)endpoint;
                _runtime = Field<SharpLinkRuntimeContext>(_server, "_runtimeContext");
                _serverRegistry = Field<ServerServiceModuleRegistry>(_server, "_serviceModuleRegistry");
                _registrationAttempted = true;
                Ensure(endpoint.RegisterAssembly(_assembly).Succeeded, "dynamic Server registration");
            }
            else
            {
                var builder = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).DisableRequestTimeout();
                _builder = builder;
                if (_clock is not null) builder.UseTimeProvider(_clock);
                _offered = true;
                builder.UseTransport(_transport);
                _buildAttempted = true;
                var endpoint = builder.Build();
                _endpoint = endpoint;
                _client = (SharpLinkClient)endpoint;
                _runtime = (SharpLinkRuntimeContext)_client.RuntimeContext;
                _clientRegistry = Field<ClientAssemblyRegistry>(_client, "_assemblyRegistry");
                _registrationAttempted = true;
                Ensure(endpoint.RegisterAssembly(_assembly).Succeeded, "dynamic Client registration");
            }
            CaptureModule();
            Ensure(_module!.TryAcquire(stream: false, out _lease), "retained dynamic module lease");
            if (_clock is not null) _clock.Module = _module;
            _callback = _module!.ForcedCancellation.Register(() =>
            {
                Interlocked.Increment(ref _cancellations);
                _cancelled.TrySetResult();
            });
            _callbackInstalled = true;
        }

        private static T Field<T>(object owner, string name)
            => (T)(owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(owner)
                ?? throw new InvalidOperationException("Missing B3a owner field: " + name));

        private object? NullableField(object owner, string name)
        {
            var field = owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("Missing B3a exit field: " + name);
            return field.GetValue(owner);
        }

        private void WithRegistry(Action action)
        {
            if (_serverRegistry is not null) { lock (_serverRegistry.Gate) action(); }
            else if (_clientRegistry is not null) { lock (Field<Lock>(_clientRegistry, "_gate")) action(); }
            else throw new InvalidOperationException("Unknown B3a registry owner");
        }

        private void CaptureModule()
        {
            if (_module is not null) return;
            WithRegistry(() =>
            {
                var found = _serverRegistry is not null
                    ? _serverRegistry.DynamicModules.TryGetValue(_assembly, out _module)
                    : _clientRegistry!.DynamicModules.TryGetValue(_assembly, out _module);
                Ensure(found && _module is not null, "exact registered B3a module");
            });
        }

        private T Own<T>(T task) where T : Task
        {
            lock (_tasksGate)
                if (!_outcomes.ContainsKey(task)) _outcomes.Add(task, Outcome(task));
            return task;
        }

        private sealed class SourceFailure(Task original, Exception raw) : Exception("B3a original task failed", raw)
        {
            internal Task Original { get; } = original;
            internal Exception Raw { get; } = raw;
        }

        private void Record(Exception failure)
        {
            if (failure is SourceFailure source)
            {
                // Keep every phase observation, but a shared original Task is
                // one source occurrence. Different Tasks throwing the SAME raw
                // object still produce independent occurrences.
                _sourceObservations.Add((source.Original, source.Raw));
                if (_reportedSources.Add(source.Original)) _failures.Add(source.Raw);
            }
            else _failures.Add(failure);
        }

        private sealed record SourceOutcome(Exception? AwaitFailure, AggregateException? FullTaskFailure)
        {
            internal Exception? Failure => FullTaskFailure ?? AwaitFailure;
        }

        private static async Task<SourceOutcome> Outcome(Task original)
        {
            try { await original.ConfigureAwait(false); return new(null, null); }
            catch (Exception failure)
            {
                // Await can expose only the first fault. Keep its actual object
                // AND Task.Exception's original full container/sibling graph.
                return new(failure, original.Exception);
            }
        }

        private async Task Join(Task original)
        {
            _ = Own(original);
            Task<SourceOutcome> outcome;
            lock (_tasksGate) outcome = _outcomes[original];
            // Diagnostic timeout is not a source outcome and stays a failure
            // even if this original completes during later containment.
            var observed = await outcome.WaitAsync(Bound);
            if (observed.Failure is { } failure) throw new SourceFailure(original, failure);
        }

        internal async Task<T> Result<T>(Task<T> original)
        {
            await Join(original);
            return await original;
        }

        internal async Task<Task<SharpLinkAssemblyUnregisterResult>> Unregister(TimeSpan budget)
        {
            if (_clock is not null) _entry = _clock.GetTimestamp();
            var invocation = Task.Factory.StartNew(() =>
            {
                var original = server ? _server!.UnregisterAssemblyAsync(_assembly, budget).AsTask()
                    : _client!.UnregisterAssemblyAsync(_assembly, budget).AsTask();
                _unregisterSource = Own(original);
                return original;
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _unregisterInvocation = Own(invocation);
            await Join(invocation);
            return await invocation;
        }

        internal async Task CheckProviderDeadline()
        {
            var unregister = await Unregister(TimeSpan.FromSeconds(5));
            await Join(_clock!.Armed.Task);
            Ensure(ReferenceEquals(_clock.Timer!.State, _module!.RetirementControl!.Driver), "exact driver armed timer");
            Ensure(_clock.Inner.EarliestTimerTimestamp == _entry + TimeSpan.FromSeconds(5).Ticks,
                "actual Change due agrees with frozen entry clock; this is not an elapsed-entry reset control");
            _unrelated!.Advance(TimeSpan.FromDays(1));
            _clock.Inner.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
            await Task.Yield();
            Ensure(!unregister.IsCompleted && Volatile.Read(ref _cancellations) == 0,
                "an unrelated clock and the owner tick before the deadline must not force calls");
            Ensure(_clock.Inner.ActiveTimerCount == 1 && _unrelated.ActiveTimerCount == 0,
                "retained lease timeout must be owned only by its RuntimeContext provider");
            _clock.Inner.Advance(TimeSpan.FromTicks(1));
            await Join(_cancelled.Task);
            var result = await Result(unregister);
            Ensure(result is { ReferencesReleased: false, RemainingCalls: 1 } && _cancellations == 1,
                "exact equality must force-cancel the retained lease once and report deferred release");
            ReleaseLease();
            await Join(_module!.WaitForDrainAsync());
            AssertCounters();
        }

        internal void ReleaseLease()
        {
            if (!_lease.IsAcquired) return;
            var lease = _lease;
            _lease = default;
            lease.Dispose();
        }

        internal void AssertCounters()
        {
            _module!.AssertAccountingInvariant();
            Ensure(_module!.RemainingCalls == 0 && _module.RemainingStreams == 0,
                "successful drain releases each retained module lease exactly once");
        }

        private B3aWave CaptureWave()
        {
            B3aWave? wave = null;
            WithRegistry(() => wave = new B3aWave(_module!));
            if (wave!.Control is not null) _ = Own(wave.Control.Driver.Task);
            if (wave.Physical is not null) _ = Own(wave.Physical);
            if (wave.Tracking is not null) _ = Own(wave.Tracking);
            if (wave.Observer is not null) _ = Own(wave.Observer);
            if (wave.Cleanup is not null) _ = Own(wave.Cleanup);
            return wave;
        }

        private async Task InvokeStop()
        {
            var invocation = Task.Factory.StartNew(() =>
            {
                var source = _endpoint is ISharpLinkServer host ? host.StopAsync(TimeSpan.Zero).AsTask()
                    : ((ISharpLinkClient)_endpoint!).StopAsync().AsTask();
                _stopSource = Own(source);
                return source;
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _stopInvocation = Own(invocation);
            await Join(invocation);
            await Join(_stopSource!);
        }

        private async Task InvokeDispose()
        {
            var invocation = Task.Factory.StartNew(() =>
            {
                var source = _endpoint!.DisposeAsync().AsTask(); // consume this ValueTask once
                _disposeSource = Own(source);
                return source;
            }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            _disposeInvocation = Own(invocation);
            await Join(invocation);
            await Join(_disposeSource!);
        }

        private async Task Attempt(Func<Task> action)
        {
            try { await action(); }
            catch (Exception failure) { _ownershipFailed = true; Record(failure); }
        }

        private void Certificate(bool condition, string message)
        {
            if (condition) return;
            _ownershipFailed = true;
            Record(new InvalidOperationException(message));
        }

        private void DataFailure(object owner, string stage, Exception? failure)
        {
            if (failure is null) return;
            _ownershipFailed = true;
            _dataObservations.Add((owner, stage, failure));
            // Re-observing the same data slot is not a new occurrence. Different
            // slots retain independent occurrences even for the same raw object.
            if (_reportedData.Add((owner, stage))) Record(failure);
        }

        private async Task Close()
        {
            var startFailures = _failures.Count;
            GC.KeepAlive(_builder);
            if (_setup is not null) await Attempt(() => Join(_setup));
            if (_setup is { IsCompleted: false }) return; // late Build/Register/acquire still owns fixture
            // Initialize is the only lease writer. Never race its struct write
            // or a late successful acquire with ReleaseLease.
            try { ReleaseLease(); } catch (Exception failure) { _ownershipFailed = true; Record(failure); }
            if (_unregisterInvocation is not null) await Attempt(() => Join(_unregisterInvocation));
            if (_unregisterInvocation is { IsCompleted: false }) return;
            // A sync Unregister throw can still have selected a real owner.
            if (_registrationAttempted)
            {
                try { CaptureModule(); _wave1 = CaptureWave(); }
                catch (Exception failure) { Record(failure); }
            }
            if (_endpoint is not null)
            {
                await Attempt(InvokeStop);
                if (_stopInvocation is { IsCompleted: false } || _stopSource is { IsCompleted: false }) return;
                await Attempt(InvokeDispose);
            }
            else if (!_offered)
            {
                await Attempt(async () => await Join(_transport.DisposeAsync().AsTask()));
            }
            else return; // Build transfer/rollback has no proven returned owner
            if (_unregisterSource is not null) await Attempt(() => Join(_unregisterSource));
            if (_disposeInvocation is { IsCompleted: false } || _disposeSource is { IsCompleted: false }) return;
            if (_module is not null)
            {
                try { _wave2 = CaptureWave(); }
                catch (Exception failure) { Record(failure); }
                if (_wave1 is not null && (_wave2 is null || !_wave1.SameTasksAs(_wave2)))
                    await Attempt(() => JoinWave(_wave1, false));
                // If both waves alias every actual Task, the final join below
                // observes those originals once, while both snapshots stay rooted.
                if (_wave2 is not null) await Attempt(() => JoinWave(_wave2, true));
            }
            await Attempt(JoinServerTails);
            // Even when a physical/observer source faulted, retain and inspect
            // every disposal source that the actual provider already returned.
            if (_clock is not null) await Attempt(() => JoinTimerDisposals(false));
            // No producer may remain before acquiring the FINAL drain task.
            if (!AllOriginalsSucceeded()) return;
            if (_clock is not null)
            {
                _ownerDrain = Own(_clock.Inner.WaitForTimersDrainedAsync());
                _unrelatedDrain = Own(_unrelated!.WaitForTimersDrainedAsync());
                await Attempt(() => Join(_ownerDrain));
                await Attempt(() => Join(_unrelatedDrain));
                await Attempt(() => JoinTimerDisposals(true));
                Certificate(_clock.Inner.ActiveTimerCount == 0 && _unrelated.ActiveTimerCount == 0,
                    "all actual provider timers exited");
            }
            // Huge uses the actual System provider. Its real driver/physical exit
            // is the certificate; no fabricated ManualTimeProvider drain.
            if (_callbackInstalled)
            {
                try { _callback.Dispose(); _callbackInstalled = false; }
                catch (Exception failure) { Record(failure); }
            }
            try
            {
                Ensure(!_registrationAttempted || (_module is not null && _wave2 is not null), "registration owner recovered");
                if (_module is not null)
                {
                    AssertCounters();
                    Ensure(_module.State == SharpLinkDynamicModuleState.Released, "actual module released");
                    WithRegistry(() =>
                    {
                        Ensure(_serverRegistry is not null ? !_serverRegistry.DynamicModules.ContainsKey(_assembly)
                            : !_clientRegistry!.DynamicModules.ContainsKey(_assembly), "exact registry release");
                        var operationsEmpty = _serverRegistry is not null
                            ? !_serverRegistry.UnregisterOperations.ContainsKey(_assembly)
                            : !Field<Dictionary<Assembly, (Task<SharpLinkAssemblyUnregisterResult> Task, SharpLinkDynamicModule Module)>>(
                                _clientRegistry!, "_unregisterOperations").ContainsKey(_assembly);
                        Ensure(operationsEmpty, "exact retirement registry receipt removed after refund");
                        Ensure(_wave2!.SameAs(_module), "no uncaptured late retirement generation");
                    });
                }
                if (_buildAttempted)
                {
                    Ensure(_stopSource is { IsCompletedSuccessfully: true } && _disposeSource is { IsCompletedSuccessfully: true }, "actual host Stop/Dispose success");
                    Ensure(_runtime is not null && Field<int>(_runtime, "_disposed") != 0, "Runtime disposed");
                    Ensure(NullableField(_runtime!, "_metadataOwner") is null && NullableField(_runtime!, "_publicationMetadataOwner") is null &&
                        NullableField(_runtime!, "_generatedManifests") is null, "Runtime metadata roots released");
                }
                Ensure(_transport.Disposal is { IsCompletedSuccessfully: true }, "offered transport actual disposal");
                if (manual) Ensure(_cancellations == 1, "no duplicate forced cancellation");
            }
            catch (Exception failure) { Record(failure); }
            if (_ownershipFailed || _failures.Count != startFailures || !AllOriginalsSucceeded()) return;
            if (_writeAttempted)
            {
                Ensure(_snapshot, "environment snapshot exists before restoration");
                try { Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", _previous); }
                catch (Exception failure) { Record(failure); return; }
            }
            if (_permit)
            {
                try { RollbackState.TestIsolation.Release(); _permit = false; }
                catch (Exception failure) { Record(failure); return; }
            }
            lock (RootsGate) Roots.Remove(this);
        }

        private async Task JoinTimerDisposals(bool final)
        {
            var timers = _clock!.Snapshot();
            // First root the full set; one failure must not hide later timers.
            foreach (var timer in timers)
                if (timer.Disposal is { } disposal) _ = Own(disposal);
            foreach (var timer in timers)
            {
                var disposal = timer.Disposal;
                if (final) Certificate(disposal is not null, "actual timer disposal source");
                if (disposal is not null) await Attempt(() => Join(disposal));
            }
        }

        private bool AllOriginalsSucceeded()
        {
            lock (_tasksGate)
                foreach (var pair in _outcomes)
                    if (!pair.Value.IsCompletedSuccessfully || pair.Value.Result.Failure is not null) return false;
            return true;
        }

        private async Task JoinServerTails()
        {
            if (_server is null) return;
            var lifecycle = Field<SharpLinkServer.ServerLifecycleCoordinator>(_server, "_lifecycle");
            var deferred = lifecycle.DeferredServiceCleanupTaskForDiagnostics;
            var observer = lifecycle.ShutdownCleanupObserverTaskForDiagnostics;
            var strictObserver = lifecycle.StrictShutdownCleanupObserverForDiagnostics;
            var terminalObserver = lifecycle.TerminalFailureObserverTaskForDiagnostics;
            var strict = lifecycle.StrictPhysicalDrainForDiagnostics;
            if (deferred is not null) _ = Own(deferred);
            if (observer is not null) _ = Own(observer);
            if (strictObserver is not null) _ = Own(strictObserver);
            if (terminalObserver is not null) _ = Own(terminalObserver);
            if (strict.Drain is not null) _ = Own(strict.Drain);
            if (deferred is not null) await Attempt(() => Join(deferred));
            if (observer is not null) await Attempt(() => Join(observer));
            if (strictObserver is not null) await Attempt(() => Join(strictObserver));
            if (terminalObserver is not null) await Attempt(() => Join(terminalObserver));
            if (strict.Drain is not null) await Attempt(() => Join(strict.Drain));
            var finalStrict = lifecycle.StrictPhysicalDrainForDiagnostics;
            Certificate(ReferenceEquals(strict.Drain, finalStrict.Drain) &&
                ReferenceEquals(deferred, lifecycle.DeferredServiceCleanupTaskForDiagnostics) &&
                ReferenceEquals(observer, lifecycle.ShutdownCleanupObserverTaskForDiagnostics) &&
                ReferenceEquals(strictObserver, lifecycle.StrictShutdownCleanupObserverForDiagnostics) &&
                ReferenceEquals(terminalObserver, lifecycle.TerminalFailureObserverTaskForDiagnostics),
                "no uncaptured server late owner");
            DataFailure(lifecycle, "strict-drain", finalStrict.Failure);
            DataFailure(lifecycle, "terminal-observer", lifecycle.StrictPhysicalTerminalFailureForDiagnostics);
            Certificate(!finalStrict.Requested || finalStrict.Observed, "server strict physical exit");
        }

        private async Task JoinWave(B3aWave wave, bool final)
        {
            // Missing stages are recorded independently, never used to skip the
            // other actual sources already captured in this wave.
            if (wave.Control is null)
                Certificate(!final && wave.Physical is null && wave.Tracking is null && wave.Observer is null,
                    "missing final retirement control is not exit");
            else
            {
                Certificate(wave.Physical is not null && wave.Tracking is not null && wave.Observer is not null,
                    "complete physical owner graph");
                var task = wave.Control.Driver.Task;
                await Attempt(() => Join(task));
                if (task.IsCompletedSuccessfully)
                {
                    var driver = task.Result;
                    DataFailure(task, "primary", driver.PrimaryFailure);
                    DataFailure(task, "cancellation", driver.CancellationFailure);
                    DataFailure(task, "timer", driver.TimerFailure);
                    DataFailure(task, "wake", driver.WakeFailure);
                    Certificate(!driver.Aborted && !driver.HasRetainedTimer, "driver actual exit and outcome data");
                }
            }
            if (wave.Physical is not null)
            {
                await Attempt(() => Join(wave.Physical));
                if (wave.Physical.IsCompletedSuccessfully)
                    Certificate(wave.Physical.Result.ReferencesReleased, "physical references released");
            }
            if (wave.Tracking is not null)
            {
                await Attempt(() => Join(wave.Tracking));
                if (wave.Tracking.IsCompletedSuccessfully)
                    DataFailure(wave.Tracking, "tracking", wave.Tracking.Result);
            }
            if (wave.Observer is not null)
            {
                await Attempt(() => Join(wave.Observer));
                if (wave.Observer.IsCompletedSuccessfully)
                {
                    var observation = wave.Observer.Result;
                    DataFailure(wave.Observer, "observer-refund", observation.Failure);
                    if (final && wave.Control is not null)
                    {
                        if (wave.Control.Origin == RetirementOrigin.Stop)
                            Certificate(wave.Receipt is null && !observation.SupervisorOwned, "Stop origin ownership");
                        else
                            Certificate(wave.Receipt is not null && observation.SupervisorOwned && wave.Receipt.IsStrictPhysical &&
                                ReferenceEquals(wave.Receipt.Task, wave.Physical) && wave.Receipt.RetainedOwnerRoot is null,
                                "public physical receipt actual release");
                    }
                }
            }
            if (final) Certificate(wave.Cleanup is not null, "module cleanup completion original");
            if (wave.Cleanup is not null)
            {
                await Attempt(() => Join(wave.Cleanup));
                if (wave.Cleanup.IsCompletedSuccessfully)
                    DataFailure(wave.Cleanup, "module-cleanup", wave.Cleanup.Result);
            }
        }

    }

    private sealed class B3aWave(SharpLinkDynamicModule module)
    {
        internal RetirementControl? Control { get; } = module.RetirementControl;
        internal Task<SharpLinkAssemblyUnregisterResult>? Physical { get; } = module.RetirementOperation;
        internal Task<Exception?>? Tracking { get; } = module.RetirementTracking;
        internal Task<(Exception? Failure, bool SupervisorOwned)>? Observer { get; } = module.RetirementObservation;
        internal FrameworkTaskRegistration? Receipt { get; } = module.RetirementRegistration;
        internal Task<Exception?>? Cleanup { get; } = module.CleanupCompletion?.Task;
        internal bool SameTasksAs(B3aWave other) => ReferenceEquals(Control, other.Control) &&
            ReferenceEquals(Physical, other.Physical) && ReferenceEquals(Tracking, other.Tracking) &&
            ReferenceEquals(Observer, other.Observer) && (Cleanup is null || ReferenceEquals(Cleanup, other.Cleanup));
        internal bool SameAs(SharpLinkDynamicModule other) => ReferenceEquals(Control, other.RetirementControl) &&
            ReferenceEquals(Physical, other.RetirementOperation) && ReferenceEquals(Tracking, other.RetirementTracking) &&
            ReferenceEquals(Observer, other.RetirementObservation) && ReferenceEquals(Receipt, other.RetirementRegistration) &&
            ReferenceEquals(Cleanup, other.CleanupCompletion?.Task);
    }

    private sealed class B3aClock : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<B3aTimer> _timers = [];
        internal ManualTimeProvider Inner { get; } = new();
        internal SharpLinkDynamicModule? Module { get; set; }
        internal TaskCompletionSource Armed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal B3aTimer? Timer { get; private set; }
        public override long TimestampFrequency => Inner.TimestampFrequency;
        public override long GetTimestamp() => Inner.GetTimestamp();
        public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();
        public override TimeZoneInfo LocalTimeZone => Inner.LocalTimeZone;
        internal B3aTimer[] Snapshot() { lock (_gate) return _timers.ToArray(); }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new B3aTimer(this, Inner.CreateTimer(callback, state, dueTime, period), state);
            lock (_gate) _timers.Add(timer);
            return timer;
        }
        internal void Changed(B3aTimer timer, TimeSpan due, bool changed)
        {
            if (!changed || due == Timeout.InfiniteTimeSpan || Module?.RetirementControl is not { } control ||
                !ReferenceEquals(timer.State, control.Driver)) return;
            lock (_gate) Timer = timer;
            Armed.TrySetResult();
        }
    }

    private sealed class B3aTimer(B3aClock owner, ITimer inner, object? state) : ITimer
    {
        internal object? State { get; } = state;
        internal Task? Disposal { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var changed = inner.Change(dueTime, period);
            owner.Changed(this, dueTime, changed);
            return changed;
        }
        public void Dispose()
        {
            inner.Dispose();
            // A real synchronous disposal receipt, not a substitute for an
            // unfinished DisposeAsync (the driver uses the latter).
            Disposal = Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            var original = inner.DisposeAsync().AsTask();
            Disposal = original;
            return new ValueTask(original);
        }
    }

    private sealed class B3aTransport : IClientTransportFactory, IServerTransportListener
    {
        internal Task? Disposal { get; private set; }
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync()
        {
            // Same stateless transport behavior as the original B3a setup, with
            // a receipt of its actual disposal (not a host ownership substitute).
            var original = ValueTask.CompletedTask.AsTask();
            Disposal = original;
            return new ValueTask(original);
        }
    }

    private static void ClearEnvironment()
    {
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
        Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
    }

    private static LoadedPlugin LoadPlugin(string name)
    {
        var context = new PluginLoadContext(name);
        return new LoadedPlugin(context, context.LoadFromAssemblyPath(typeof(RollbackMarker).Assembly.Location));
    }

    private static Exception Capture(Action action)
    {
        try { action(); throw new Exception("expected rollback failure"); }
        catch (Exception exception) { return exception; }
    }

    private static bool Contains(Exception exception, string text)
    {
        if (exception.Message.Contains(text, StringComparison.Ordinal)) return true;
        if (exception is AggregateException aggregate)
            foreach (var inner in aggregate.InnerExceptions) if (Contains(inner, text)) return true;
        return exception.InnerException is { } nested && Contains(nested, text);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class PluginLoadContext(string name) : AssemblyLoadContext(name, isCollectible: true)
    {
        protected override Assembly? Load(AssemblyName assemblyName) => null;
    }

    private sealed record LoadedPlugin(AssemblyLoadContext Context, Assembly Assembly) : IDisposable
    {
        public void Dispose() => Context.Unload();
    }

    private sealed class NoopClientTransport : IClientTransportFactory
    {
        public ValueTask<ITransportConnection> ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoopServerTransport : IServerTransportListener
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class ThrowingProfileServerTransport : IServerTransportListener, IPerformanceProfileAwareTransport
    {
        public EndPoint? LocalEndPoint => null;
        public ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromException<ITransportConnection>(new NotSupportedException());
        public void BindPerformanceProfile(SharpLinkPerformanceProfile profile)
            => throw new InvalidOperationException("server profile binding failed");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
