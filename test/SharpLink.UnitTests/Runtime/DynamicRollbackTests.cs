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
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            var client = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableRequestTimeout()
                .UseTransport(new NoopClientTransport()).Build();
            using var loaded = LoadPlugin("client-registration");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "first-schema");
                Ensure(client.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "first Client registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "second-schema");

                var failure = Capture(() => client.RegisterAssembly(loaded.Assembly));

                Ensure(Contains(failure, "Codec conflict"), "Client rollback retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Client rollback retains Adapter cleanup failure");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
                try { await client.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ServerRegistrationRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new NoopServerTransport()).Build();
            using var loaded = LoadPlugin("server-registration");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "first-schema");
                Ensure(server.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "first Server registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "second-schema");

                var failure = Capture(() => server.RegisterAssembly(loaded.Assembly));

                Ensure(Contains(failure, "Codec conflict"), "Server rollback retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server rollback retains Adapter cleanup failure");
            }
            finally
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", null);
                try { await server.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ClientReplacementRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            var client = SharpClientBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
                .DisableRequestTimeout()
                .UseTransport(new NoopClientTransport()).Build();
            using var oldPlugin = LoadPlugin("client-old");
            using var newPlugin = LoadPlugin("client-new");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "retained-schema");
                Ensure(client.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "retained Client registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
                Ensure(client.RegisterAssembly(oldPlugin.Assembly).Succeeded, "old Client registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "incoming-schema");

                var failure = Capture(() => client.ReplaceAssemblyAsync(
                    oldPlugin.Assembly, newPlugin.Assembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult());

                Ensure(Contains(failure, "Codec conflict"), "Client replacement retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Client replacement retains Adapter cleanup failure");
            }
            finally
            {
                ClearEnvironment();
                try { await client.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
    }

    [Test]
    public async Task ServerReplacementRollbackShouldPreserveConflictAndAdapterCleanupFailure(CancellationToken testCancellationToken)
    {
        await RollbackState.TestIsolation.WaitAsync(testCancellationToken);
        try
        {
            var server = SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty).UseTransport(new NoopServerTransport()).Build();
            using var oldPlugin = LoadPlugin("server-old");
            using var newPlugin = LoadPlugin("server-new");
            try
            {
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "retained-schema");
                Ensure(server.RegisterAssembly(typeof(RollbackMarker).Assembly).Succeeded, "retained Server registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", "1");
                Ensure(server.RegisterAssembly(oldPlugin.Assembly).Succeeded, "old Server registration");
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_DISABLE_CODEC", null);
                Environment.SetEnvironmentVariable("SHARPLINK_ROLLBACK_SCHEMA", "incoming-schema");

                var failure = Capture(() => server.ReplaceAssemblyAsync(
                    oldPlugin.Assembly, newPlugin.Assembly, TimeSpan.Zero).AsTask().GetAwaiter().GetResult());

                Ensure(Contains(failure, "Codec conflict"), "Server replacement retains the structured conflict");
                Ensure(Contains(failure, "rollback Adapter scope cleanup failed"), "Server replacement retains Adapter cleanup failure");
            }
            finally
            {
                ClearEnvironment();
                try { await server.DisposeAsync(); } catch { }
            }
        }
        finally
        {
            RollbackState.TestIsolation.Release();
        }
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
