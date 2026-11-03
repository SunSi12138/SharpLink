using System.Collections.Frozen;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Loader;
using SharpLink.Client;
using SharpLink.RetirementEntryPlugin;

namespace SharpLink.UnitTests.Client;

[NotInParallel("retirement-entry-plugin")]
public sealed partial class D2AssemblyRetirementTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly SemaphoreSlim Isolation = new(1, 1);
    private static readonly List<Fixture> Retained = [];
    private static async Task Run(Func<Fixture, Task> body, bool secondary = false, TimeProvider? provider = null, bool decorate = false)
    {
        Ensure(await Isolation.WaitAsync(Bound), "bounded fixture admission");
        var f = new Fixture(secondary, provider, decorate);
        Exception? failure = null;
        try { await f.Result(f.Invoke(f.Initialize)); await body(f); }
        catch (Exception error) { failure = error; }
        try { await f.Close(failure); }
        finally
        {
            if (f.Ended) Isolation.Release();
            else lock (Retained) Retained.Add(f);
        }
    }

    private sealed class Fixture(bool secondary, TimeProvider? provider, bool decorate)
    {
        private readonly Lock _gate = new();
        private readonly List<Task> _tasks = [];
        private readonly List<Task> _invocations = [];
        private readonly Dictionary<Task, Exception> _expected = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<Task, Task<Exception?>> _outcomes = new(ReferenceEqualityComparer.Instance);
        private readonly List<object> _owners = [];
        private readonly List<SharpLinkDynamicModule> _modules = [];
        private readonly List<SharpLinkClient> _children = [];
        private readonly List<IDisposable> _extraOwners = [];
        private readonly List<Plugin> _plugins = [];
        private readonly List<Gate> _gates = [];
        private readonly List<CancellationTokenRegistration> _callbacks = [];
        private SharpLinkDynamicModuleLease _call;
        private Task? _stop;
        internal bool Ended;
        private bool _expectedRetainedStop;
        internal SharpLinkMultiClusterClient Multi = null!;
        internal SharpLinkClient Child = null!;
        internal D2ThrowingDecorator? Decorator;
        internal SharpLinkClient OtherChild = null!;
        internal ClientAssemblyRegistry Registry = null!;
        internal SharpLinkDynamicModule Module = null!;
        internal readonly Assembly OldAssembly = typeof(EntryManifest).Assembly;
        internal readonly SharpLinkClusterKey Key = new("d2-owner");
        internal readonly SharpLinkClusterKey OtherKey = new("d2-other");
        internal Action<string, object>? ParentHook;
        internal Action<string>? ChildHook;
        internal object LastOwner { get { lock (_gate) return _owners[^1]; } }
        internal int OwnerCount { get { lock (_gate) return _owners.Count; } }
        internal ColdMetadataAuthority Authority => Read<ColdMetadataDomain>(Multi, "_metadataDomain").Authority;

        internal bool Initialize()
        {
            Ensure(EntryState.ManifestEntered is null && EntryState.ManifestRelease is null &&
                EntryState.StubFailure is null && EntryState.ScopeFailure is null && EntryState.ScopeDisposing is null, "default plugin is uninjected");
            if (decorate)
            {
                Ensure(!secondary, "the narrow decorator fixture has one actual child");
                Decorator = new D2ThrowingDecorator(this, provider ?? TimeProvider.System);
                var slots = new Dictionary<SharpLinkClusterKey, SharpLinkClusterSlot>
                { [Key] = new(Key, Decorator, true) }.ToFrozenDictionary();
                Multi = new SharpLinkMultiClusterClient(new SharpLinkMultiClusterOptions(), slots,
                    FrozenDictionary<Type, SharpLinkClusterRouteRegistration>.Empty, []);
                // As in the existing integration decorator, the first exact
                // prepared manifest supplies the shared authority to the child.
                Ensure(Multi.RegisterAssembly(Key, OldAssembly).Succeeded, "actual decorated prepared registration");
                Child = Decorator.Inner;
            }
            else
            {
                var builder = SharpLinkMultiClusterClientBuilder.Create()
                    .UseGeneratedDiscoverySources(FixedGeneratedManifestSource.Empty, FixedGeneratedClusterRouteSource.Empty)
                    .DisableRequestTimeout()
                    .AddCluster(Key, child => child.UseTimeProvider(provider ?? TimeProvider.System).UseTcp(IPAddress.Loopback.ToString(), 1),
                        slot => slot.AllowDynamicContracts = true);
                if (secondary)
                    builder.AddCluster(OtherKey, child => child.UseTcp(IPAddress.Loopback.ToString(), 1),
                        slot => slot.AllowDynamicContracts = true);
                Multi = (SharpLinkMultiClusterClient)builder.Build();
                WithSnapshot(snapshot =>
                {
                    Child = (SharpLinkClient)snapshot.Clusters[Key].Client;
                    if (secondary) OtherChild = (SharpLinkClient)snapshot.Clusters[OtherKey].Client;
                    return true;
                });
                Ensure(Multi.RegisterAssembly(Key, OldAssembly).Succeeded, "real prepared child and coordinator publication");
            }
            CaptureChildren();
            Registry = Read<ClientAssemblyRegistry>(Child, "_assemblyRegistry");
            lock (Read<Lock>(Registry, "_gate")) Module = Registry.DynamicModules[OldAssembly];
            RememberModule(Module);
            Multi.AssemblyRetirementForDiagnostics = (stage, owner) =>
            {
                if (stage == "BeforeTracking")
                {
                    lock (_gate) _owners.Add(owner);
                    Own(Read<Task>(owner, "Worker")); Own(Read<Task>(owner, "Observer"));
                }
                ParentHook?.Invoke(stage, owner);
            };
            Registry.AssemblyConstructionForDiagnostics = stage => ChildHook?.Invoke(stage);
            return true;
        }
        internal T OwnResource<T>(T resource) where T : IDisposable
        { lock (_gate) _extraOwners.Add(resource); return resource; }
        internal Task<T> Own<T>(Task<T> task) { Own((Task)task); return task; }
        internal Task Own(Task task)
        {
            lock (_gate) if (!_tasks.Contains(task)) _tasks.Add(task);
            return task;
        }
        internal Task<T> Invoke<T>(Func<T> body)
        {
            var original = Task.Factory.StartNew(body, CancellationToken.None,
                TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            lock (_gate) _invocations.Add(original);
            return Own(original);
        }
        internal Task<SharpLinkAssemblyReplacementResult> Replace(Assembly replacement, CancellationToken token = default)
            => Own(Multi.ReplaceAssemblyAsync(Key, OldAssembly, replacement, TimeSpan.Zero, token).AsTask());
        internal Task<SharpLinkAssemblyUnregisterResult> Unregister(TimeSpan? budget = null, CancellationToken token = default)
            => Own(Multi.UnregisterAssemblyAsync(Key, OldAssembly, budget ?? TimeSpan.Zero, token).AsTask());
        internal Task Stop()
        { lock (_gate) return _stop ??= Own(Multi.StopAsync().AsTask()); }
        internal Plugin NewPlugin(bool blocked = false)
        { var result = new Plugin(blocked); lock (_gate) _plugins.Add(result); return result; }
        internal Gate NewGate()
        { var result = new Gate(); lock (_gate) _gates.Add(result); return result; }
        internal void HoldCall()
        {
            lock (_gate)
            {
                Ensure(!_call.IsAcquired && Module.TryAcquire(false, out _call), "one actual module call acquired");
            }
        }
        internal void ReleaseCall()
        {
            lock (_gate)
            {
                if (!_call.IsAcquired) return;
                var original = _call; _call = default; original.Dispose();
            }
        }
        internal void OnCancellation(Action callback)
        { var registration = Module.ForcedCancellation.Register(callback); lock (_gate) _callbacks.Add(registration); }
        private void RememberModule(SharpLinkDynamicModule module)
        { lock (_gate) if (!_modules.Contains(module)) _modules.Add(module); }
        internal T WithSnapshot<T>(Func<MultiClusterSnapshot, T> read)
        {
            MultiClusterSnapshot snapshot;
            lock (Read<Lock>(Multi, "_gate")) snapshot = Read<MultiClusterSnapshot>(Multi, "_snapshot").Retain();
            try { return read(snapshot); } finally { snapshot.Dispose(); }
        }
        internal void CaptureChildren()
        {
            if (Multi is null) return;
            WithSnapshot(snapshot =>
            {
                lock (_gate)
                    foreach (var slot in snapshot.Slots)
                    {
                        var child = slot.Client switch
                        {
                            SharpLinkClient actual => actual,
                            D2ThrowingDecorator decorated => decorated.Inner,
                            _ => throw new InvalidOperationException("Unknown fixture child ownership.")
                        };
                        if (!_children.Contains(child)) _children.Add(child);
                    }
                return true;
            });
        }
        internal bool RoutesTo(Assembly assembly) => WithSnapshot(snapshot => snapshot.Routes.Values.Any(route =>
            ReferenceEquals(route.OwnerAssembly, assembly)));
        internal MultiClusterSnapshot SnapshotIdentity => WithSnapshot(static snapshot => snapshot);
        internal void Harvest(object owner)
        {
            var receipt = Read<ChildAssemblySelectionMailbox>(owner, "Mailbox");
            if (receipt.Control is { } control)
            {
                RememberModule(control.Module);
                if (control.CreatedDriver is { } driver) Own(driver.Task);
            }
            if (receipt.Physical is { } physical) Own(physical);
            if (receipt.Observation is { } observation) Own(observation);
        }
        internal async Task JoinOwner(object owner)
        {
            Harvest(owner);
            await Within(Read<Task>(owner, "Worker"));
            await Within(Read<Task>(owner, "Observer"));
            Harvest(owner);
        }
        internal async Task<Exception?> Failure(Task original)
        {
            Task<Exception?> observation;
            lock (_gate)
            {
                if (!_outcomes.TryGetValue(original, out observation!))
                    _outcomes.Add(original, observation = ObserveOriginal(original));
            }
            try { return await observation.WaitAsync(Bound).ConfigureAwait(false); }
            catch (Exception diagnostic)
            {
                throw new InvalidOperationException("Diagnostic wait ended before original outcome observation.", diagnostic);
            }
        }
        private static async Task<Exception?> ObserveOriginal(Task original)
        {
            try { await original.ConfigureAwait(false); return null; }
            catch (Exception failure) { return OriginalFailure(original, failure); }
        }
        internal async Task Within(Task original)
        { if (await Failure(original) is { } failure) ExceptionDispatchInfo.Capture(failure).Throw(); }
        internal async Task<T> Result<T>(Task<T> original)
        { await Within(original); return await original.ConfigureAwait(false); }
        internal async Task ExpectFault(Task original, Exception marker)
        {
            var failure = await Failure(original);
            Ensure(failure is not null && Count(failure, marker) == 1 && Only(failure, marker), "exact one source marker, no extra sibling");
            lock (_gate) _expected.Add(original, marker);
        }
        internal async Task ExpectGraph(Task original, params Exception[] expected)
        {
            var failure = await Failure(original);
            Ensure(failure is not null && OnlyAny(failure, expected) && expected.All(marker =>
                Count(failure, marker) == expected.Count(value => ReferenceEquals(value, marker))),
                "complete original sibling graph has exactly the selected source occurrences");
            lock (_gate) _expected.Add(original, failure!);
        }
        internal async Task ExpectOccurrences(Task original, Exception marker, int count)
        {
            var failure = await Failure(original);
            Ensure(failure is not null && Count(failure, marker) == count && Only(failure, marker),
                "exact independent source occurrence count and complete original graph");
            lock (_gate) _expected.Add(original, marker);
        }
        internal void AcceptInspectedRetainedStop(Exception exact)
        {
            Ensure(_stop is { IsFaulted: true } && exact.GetType().Name == "AssemblyStopFailure",
                "only the inspected real typed failed Stop can authorize deliberate test quarantine");
            lock (_gate) _expected.Add(_stop!, exact);
            _expectedRetainedStop = true;
        }
        internal async Task ExpectCallerCancellation(Task original, CancellationToken caller)
        {
            var failure = await Failure(original);
            Ensure(caller.IsCancellationRequested && failure is OperationCanceledException, "caller wrapper cancellation");
            lock (_gate) _expected.Add(original, failure!);
        }
        internal async Task Close(Exception? bodyFailure)
        {
            List<Exception> failures = [];
            if (bodyFailure is not null) failures.Add(bodyFailure);
            Gate[] gates; Plugin[] plugins; Task[] invocations;
            lock (_gate) { gates = _gates.ToArray(); plugins = _plugins.ToArray(); invocations = _invocations.ToArray(); }
            foreach (var gate in gates) gate.Release.Set();
            foreach (var plugin in plugins) plugin.Release.TrySetResult();
            try { ReleaseCall(); } catch (Exception failure) { failures.Add(failure); }
            async Task Join(Task original)
            {
                try
                {
                    if (await Failure(original) is { } failure)
                    {
                        Exception? expected; lock (_gate) _expected.TryGetValue(original, out expected);
                        if (expected is null || !Only(failure, expected)) failures.Add(failure);
                    }
                }
                catch (Exception diagnostic) { failures.Add(diagnostic); }
            }
            foreach (var invocation in invocations) await Join(invocation);
            if (invocations.Any(static task => !task.IsCompleted))
            { failures.Add(new InvalidOperationException("Original creator still owns possible future resources.")); throw new AggregateException(failures); }
            ParentHook = null; ChildHook = null;
            IDisposable[] extraOwners; lock (_gate) extraOwners = _extraOwners.ToArray();
            foreach (var resource in extraOwners)
                try { resource.Dispose(); } catch (Exception failure) { failures.Add(failure); }
            CaptureChildren();
            SharpLinkClient[] children; lock (_gate) children = _children.ToArray();
            foreach (var child in children)
            {
                var registry = Read<ClientAssemblyRegistry>(child, "_assemblyRegistry");
                lock (Read<Lock>(registry, "_gate"))
                    foreach (var module in registry.DynamicModules.Values) RememberModule(module);
            }
            object[] owners; lock (_gate) owners = _owners.ToArray();
            foreach (var owner in owners) Harvest(owner);
            // Coordinator owns ordering: never pre-stop children to hide a D2 join bug.
            if (Multi is not null)
            {
                try { _ = Stop(); }
                catch (Exception failure) { failures.Add(failure); }
                if (_stop is not null) await Join(_stop);
            }
            // A certified Abort may install its one successor while cleanup
            // releases a held gate. Stop seals that source; capture actual new
            // roots after the original Stop join, not just the earlier sample.
            lock (_gate) owners = _owners.ToArray();
            foreach (var owner in owners) Harvest(owner);
            Task[] originals; SharpLinkDynamicModule[] modules;
            lock (_gate) { originals = _tasks.ToArray(); modules = _modules.ToArray(); }
            foreach (var original in originals) await Join(original);
            var released = children.All(child =>
                Read<int>(Read<SharpLinkRuntimeContext>(child, "_runtimeContext"), "_disposed") != 0);
            Task[] observations; lock (_gate) observations = _outcomes.Values.Cast<Task>().ToArray();
            Ended = (Multi is null || _stop is { IsCompletedSuccessfully: true }) && originals.All(static task => task.IsCompleted) &&
                observations.All(static task => task.IsCompleted) && released && modules.All(static module =>
                    module.State == SharpLinkDynamicModuleState.Released && module.RetirementControl is
                        { PhysicalExited: true, ReservationLeaseReturned: true, CapacityRefunded: true });
            if (Ended)
            {
                foreach (var callback in _callbacks)
                    try { callback.Dispose(); } catch (Exception failure) { failures.Add(failure); }
                foreach (var plugin in plugins)
                    try { plugin.Dispose(); } catch (Exception failure) { failures.Add(failure); }
                foreach (var gate in gates)
                    try { gate.Release.Dispose(); } catch (Exception failure) { failures.Add(failure); }
                if (failures.Count != 0) Ended = false; // Retain failed cleanup identity/permit, not a guessed refund.
            }
            // Intentional failclosed controls MUST be executed in isolated
            // processes. They keep this fixture and semaphore permit rooted;
            // they do not pretend a faulted Stop released live metadata.
            var retainedTerminal = _expectedRetainedStop && bodyFailure is null && _stop is { IsFaulted: true } &&
                originals.All(static task => task.IsCompleted) && observations.All(static task => task.IsCompleted);
            if (!Ended && !retainedTerminal && failures.Count == 0)
                failures.Add(new InvalidOperationException("Exact physical/module/accounting exit is unproven."));
            if (failures.Count == 1) ExceptionDispatchInfo.Capture(failures[0]).Throw();
            if (failures.Count != 0) throw new AggregateException(failures);
        }
    }
    private sealed class Gate
    {
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualResetEventSlim Release = new(false);
        internal void Wait() { Entered.TrySetResult(); Release.Wait(); }
    }
    private sealed class Plugin : IDisposable
    {
        private readonly AssemblyLoadContext _context = new("d2-control-" + Guid.NewGuid(), isCollectible: true);
        private readonly Type _state;
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Assembly Assembly { get; }
        internal Plugin(bool blocked)
        {
            try
            {
                Assembly = _context.LoadFromAssemblyPath(typeof(EntryManifest).Assembly.Location);
                _state = Assembly.GetType(typeof(EntryState).FullName!, throwOnError: true)!;
                _state.GetField(nameof(EntryState.ManifestEntered))!.SetValue(null, Entered);
                _state.GetField(nameof(EntryState.ManifestRelease))!.SetValue(null, Release);
                if (!blocked) Release.TrySetResult();
            }
            catch { _context.Unload(); throw; }
        }
        internal void SetScopeDisposing(Action callback)
            => _state.GetField(nameof(EntryState.ScopeDisposing))!.SetValue(null, callback);
        internal int Count(string name) => (int)_state.GetField(name)!.GetValue(null)!;
        public void Dispose()
        {
            _state.GetField(nameof(EntryState.ManifestEntered))!.SetValue(null, null);
            _state.GetField(nameof(EntryState.ManifestRelease))!.SetValue(null, null);
            _state.GetField(nameof(EntryState.ScopeDisposing))!.SetValue(null, null);
            _context.Unload();
        }
    }
    private static T Read<T>(object value, string field) => (T)(value.GetType()
        .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(value)
        ?? throw new InvalidOperationException("Missing exact field " + field));
    private static T? ReadOptional<T>(object value, string field) where T : class
        => (T?)(value.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new InvalidOperationException("Missing exact field " + field)).GetValue(value);
    private readonly record struct StopOccurrence(string Stage, object Owner, Task? Original, Exception? Failure, int OuterStageIndex);
    private static StopOccurrence[] StopOccurrences(Exception error)
    {
        Ensure(error.GetType().Name == "AssemblyStopFailure", "exact typed D2 Stop error, not an Aggregate substitute");
        var values = (System.Collections.IEnumerable)(error.GetType().GetProperty("Occurrences",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(error)!);
        return values.Cast<object>().Select(value => new StopOccurrence(
            Property(value, "Stage")!.ToString()!, Property(value, "Owner")!,
            (Task?)Property(value, "Original"), (Exception?)Property(value, "Failure"),
            (int)Property(value, "OuterStageIndex")!)).ToArray();
    }
    private static object? Property(object value, string name) => (value.GetType().GetProperty(name,
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
        ?? throw new InvalidOperationException("Missing exact property " + name)).GetValue(value);
    private static Exception OriginalFailure(Task original, Exception awaited)
    {
        if (original.Exception is not { } graph) return PhysicalTaskFailureException.GetRaw(awaited);
        return graph.InnerExceptions.Count == 1 ? PhysicalTaskFailureException.GetRaw(graph.InnerExceptions[0]) : graph;
    }
    private static int Count(Exception error, Exception marker)
        => ReferenceEquals(error, marker) ? 1 : error is PhysicalTaskFailureException
            ? Count(PhysicalTaskFailureException.GetRaw(error), marker)
            : error is AggregateException aggregate ? aggregate.InnerExceptions.Sum(child => Count(child, marker)) : 0;
    private static bool OnlyAny(Exception error, Exception[] expected)
        => expected.Any(marker => ReferenceEquals(error, marker)) || (error is PhysicalTaskFailureException
            ? OnlyAny(PhysicalTaskFailureException.GetRaw(error), expected)
            : error is AggregateException aggregate && aggregate.InnerExceptions.Count != 0 &&
                aggregate.InnerExceptions.All(child => OnlyAny(child, expected)));
    private static bool Only(Exception error, Exception marker)
        => ReferenceEquals(error, marker) || (error is PhysicalTaskFailureException
            ? Only(PhysicalTaskFailureException.GetRaw(error), marker)
            : error is AggregateException aggregate && aggregate.InnerExceptions.Count != 0 && aggregate.InnerExceptions.All(child => Only(child, marker)));
    private static void Ensure(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("D2 control: " + message); }
}
