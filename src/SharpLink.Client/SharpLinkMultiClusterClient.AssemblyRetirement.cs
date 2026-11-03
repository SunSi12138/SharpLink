using System.Reflection;
using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    // Optional instance-local cold diagnostics; never invoked by RPC dispatch.
    internal Action<string, object>? AssemblyRetirementForDiagnostics { get; set; }
    private CoordinatorAssemblyRetirement? _assemblyOwners;
    private CoordinatorAssemblyRetirement? _assemblyWindow;
    private CoordinatorAssemblyCapacity? _failedAssemblyCapacities;
    private DynamicAssemblyRegistration? _failedAssemblyRegistrations;
    private bool _assemblySealed;
    private bool _assemblyPublicationFailed;
    private int _assemblyOwnerCount;
    private int _assemblyInventoryCount;
    private ColdMetadataReservation _assemblyOperationMapCapacity = null!;
    private ColdMetadataReservation _assemblyDrainingMapCapacity = null!;
    private ColdMetadataReservation _assemblyRegistrationListCapacity = null!;
    private AssemblyStopEntry[] _assemblyStopEntries = [];
    private readonly Exception?[] _assemblyStopFailures;

    private enum AssemblyFailureStage : byte
    {
        Initialization, Prebuild, Invocation, WindowClaim, Handoff, Swap,
        OldSnapshot, OldRegistration, CandidateSnapshot, CandidateRegistration,
        Preparation, Tracking, TrackingCompletion, TokenDetach, TokenRefund, Shutdown
    }
    private readonly record struct AssemblyPublicOutcome(
        SharpLinkAssemblyUnregisterResult Unregister, SharpLinkAssemblyReplacementResult Replacement,
        Exception?[] IndependentFailures, ushort FailureMask, Exception? PhysicalFailure, Exception? ObserverFailure);
    private struct AssemblyStopEntry
    {
        internal SharpLinkClusterSlot? Slot;
        internal Task? Original;
        internal Exception? Failure;
        internal bool MetadataReleaseAttempted;
    }

    private CoordinatorAssemblyCapacity CreateAssemblyCapacityLocked(ColdMetadataPreparation preparation)
    {
        if (_assemblySealed) throw new InvalidOperationException("Assembly retirement admission is sealed.");
        if (_assemblyInventoryCount >= checked(_options.MaxClusters * 4096)) throw ColdMetadataException.Capacity();
        var token = new CoordinatorAssemblyCapacity(this);
        _assemblyInventoryCount++;
        try { token.Initialize(preparation); return token; }
        catch (Exception primary)
        {
            try { token.Dispose(); }
            catch (Exception cleanup)
            {
                var graph = new AggregateException(primary, cleanup);
                token.PreserveAdmissionFailure(graph);
                token.FailedNext = _failedAssemblyCapacities;
                _failedAssemblyCapacities = token;
                throw graph;
            }
            throw;
        }
    }

    internal void RetainFailedAssemblyRegistration(DynamicAssemblyRegistration registration)
    {
        // Intrusive links and error fields are prepaid in this registration's
        // existing96 custody. No allocation, callback or new reserve here.
        lock (_gate)
        {
            _assemblyPublicationFailed = true;
            if (registration.CustodyRetained) return;
            registration.CustodyRetained = true;
            registration.FailedNext = _failedAssemblyRegistrations;
            _failedAssemblyRegistrations = registration;
        }
    }

    private Exception RollbackAssemblySlotClonesLocked(DynamicAssemblyRegistration[] originals,
        SharpLinkClusterSlot candidate, Exception primary)
    {
        // A new D2 lease failure must never leave live registration rows pointing
        // at a candidate which the caller will physically roll back. The original
        // immutable route snapshot/child remains authoritative; close cold writers
        // because earlier records may already have completed their lease release.
        _assemblyPublicationFailed = true;
        Exception graph = primary;
        foreach (var original in originals)
        {
            for (var i = 0; i < _dynamicRegistrations.Count; i++)
            {
                var clone = _dynamicRegistrations[i];
                if (!ReferenceEquals(clone.Slot, candidate) ||
                    !ReferenceEquals(clone.Assembly, original.Assembly)) continue;
                _dynamicRegistrations[i] = original;
                try { clone.Dispose(); }
                catch (Exception cleanup) { graph = new AggregateException(graph, cleanup); }
                break;
            }
        }
        return graph;
    }

    internal void RefundAssemblyInventory()
    {
        // Only after the token's authority lock has been released; no reverse
        // authority -> coordinator lock edge. Cold ownership only.
        lock (_gate)
        {
            if (_assemblyInventoryCount <= 0) throw new InvalidOperationException("Assembly inventory accounting underflow.");
            _assemblyInventoryCount--;
        }
    }

    private CoordinatorAssemblyRetirement StartAssemblyOwnerLocked(
        DynamicAssemblyRegistration registration, ClientRetirementBudget budget,
        PreparedGeneratedManifest? replacement, CoordinatorAssemblyRetirement? predecessor = null)
    {
        if (_assemblySealed || _assemblyPublicationFailed || _assemblyWindow is not null || _slotMutationInProgress)
            throw new InvalidOperationException("A coordinator assembly publication cannot be admitted.");
        if (predecessor is null && _assemblyOwnerCount >= checked(_options.MaxClusters * 4096))
            throw ColdMetadataException.Capacity();
        CoordinatorAssemblyRetirement? precedingRoot = null;
        if (_unregisterOperations.TryGetValue(registration, out var current))
        {
            if (predecessor is null)
            {
                if (replacement is null && current.Replacement is null) return current;
                throw new InvalidOperationException("The exact assembly already has a selected retirement owner.");
            }
            if (!ReferenceEquals(current, predecessor) || !predecessor.Certified ||
                predecessor.Replacement is not null || predecessor.Predecessor is not null ||
                predecessor.SuccessorCandidate is not null || predecessor.Public.Task.IsCompleted)
                throw new InvalidOperationException("The successor does not own the exact certified predecessor.");
            var cursor = _assemblyOwners;
            while (cursor is not null && !ReferenceEquals(cursor, predecessor))
            { precedingRoot = cursor; cursor = cursor.Next; }
            if (cursor is null) throw new InvalidOperationException("The predecessor is not durably rooted.");
        }
        else if (predecessor is not null)
            throw new InvalidOperationException("The predecessor lost its dictionary selection.");
        registration.AttachCustodyOwner(this);
        if (registration.HasUncertifiedClone)
            throw new InvalidOperationException("The registration retains uncertified clone cleanup.");
        registration.Capacity ??= CreateAssemblyCapacityLocked(((PreparedGeneratedManifest)registration.Manifest).Preparation);
        if (!registration.Capacity.CanClaim)
            throw new InvalidOperationException("Assembly capacity has an uncertified prior owner.");
        var operationCapacity = checked(_unregisterOperations.Count + (predecessor is null ? 1 : 0));
        var drainingCapacity = checked(_drainingRegistrations.Count + (predecessor is null ? 1 : 0));
        // Conservative prime-table upper bound: at most 2N entry capacity,
        // with two retained references per dictionary entry (minimum three).
        // Paid before allocation and retained by storage, not individual rows.
        _assemblyOperationMapCapacity.GrowTo(Math.Max(6, checked(4 * operationCapacity)));
        _assemblyDrainingMapCapacity.GrowTo(Math.Max(3, checked(2 * drainingCapacity)));
        _unregisterOperations.EnsureCapacity(operationCapacity);
        _drainingRegistrations.EnsureCapacity(drainingCapacity);
        // Constructor allocations and metadata retain precede transfer and Start.
        var owner = new CoordinatorAssemblyRetirement(this, registration, budget, replacement,
            predecessor?.Public, predecessor?.Observer);
        owner.Capacity = registration.Capacity;
        registration.Capacity = null;
        if (predecessor is null)
        {
            owner.Next = _assemblyOwners;
            _assemblyOwners = owner;
            _assemblyOwnerCount++;
            _unregisterOperations.Add(registration, owner);
            _drainingRegistrations.Add(registration);
            if (replacement is not null) _activeAssemblyReplacements++;
        }
        else predecessor.SuccessorCandidate = owner; // Actual prestart roots remain discoverable.
        // Both actual original tasks first await the prebuilt start decision.
        if (ExecutionContext.IsFlowSuppressed())
        { owner.Worker = owner.RunAsync(); owner.Observer = owner.ObserveAsync(); }
        else
        {
            using (ExecutionContext.SuppressFlow())
            { owner.Worker = owner.RunAsync(); owner.Observer = owner.ObserveAsync(); }
        }
        if (predecessor is not null)
        {
            // All fallible construction and original task creation precedes this
            // atomic handoff. The old Observer has no Track/cleanup tail after it.
            owner.Next = predecessor.Next;
            if (precedingRoot is null) _assemblyOwners = owner;
            else precedingRoot.Next = owner;
            predecessor.Next = null;
            _unregisterOperations[registration] = owner;
            predecessor.SuccessorCandidate = null;
        }
        owner.Installed.TrySetResult();
        return owner;
    }

    private sealed class CoordinatorAssemblyRetirement
    {
        private readonly SharpLinkMultiClusterClient _parent;
        internal readonly DynamicAssemblyRegistration Registration;
        internal readonly ClientRetirementBudget Budget;
        internal readonly PreparedGeneratedManifest? Replacement;
        internal readonly ChildAssemblySelectionMailbox Mailbox;
        // Two precharged adapter slots from the structural allowance: this gate
        // only admits a ROOTED original worker, which owns its own Track decision.
        internal readonly TaskCompletionSource Installed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<bool> Start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<AssemblyPublicOutcome> Public;
        internal Task? Predecessor;
        internal readonly TaskCompletionSource<bool> PublicationDone = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly Exception?[] Failures = new Exception?[16];
        internal CoordinatorAssemblyRetirement? Next;
        internal CoordinatorAssemblyRetirement? SuccessorCandidate;
        internal CoordinatorAssemblyCapacity Capacity = null!;
        internal FrameworkTaskRegistration? Supervision;
        internal Task Worker = null!;
        internal Task Observer = null!;
        internal bool Certified;
        internal bool StopSelected;
        private IDisposable? _incomingLease;
        private ColdMetadataPreparation? _storage;
        private MultiClusterSnapshot? _sourceSnapshot;
        private MultiClusterSnapshot? _candidateSnapshot;
        private MultiClusterSnapshot? _oldSnapshot;
        private DynamicAssemblyRegistration? _candidateRegistration;
        private bool _published;
        private bool _claimed;
        private bool _childReturned;
        private bool _cleanupCertified = true;
        private bool _retry;
        private Exception? _physicalFailure;
        private Exception? _observerFailure;
        private SharpLinkAssemblyUnregisterResult _unregister;
        private SharpLinkAssemblyReplacementResult _replacement;

        internal CoordinatorAssemblyRetirement(SharpLinkMultiClusterClient parent,
            DynamicAssemblyRegistration registration, ClientRetirementBudget budget, PreparedGeneratedManifest? replacement,
            TaskCompletionSource<AssemblyPublicOutcome>? logical = null, Task? predecessor = null)
        {
            Public = logical ?? new(TaskCreationOptions.RunContinuationsAsynchronously);
            Predecessor = predecessor;
            _parent = parent; Registration = registration; Budget = budget; Replacement = replacement;
            Mailbox = new ChildAssemblySelectionMailbox(ClaimWindow, FinalizePublication);
            _incomingLease = replacement?.Preparation.Retain();
        }
        internal void Record(AssemblyFailureStage stage, Exception failure)
        {
            var index = (int)stage;
            // Each stage has exactly one elected execution. Do not reference-dedup
            // the same exception object raised by two independent stages.
            if (Failures[index] is not null)
                throw new InvalidOperationException("An assembly stage attempted duplicate outcome publication.");
            Failures[index] = failure;
        }
        private ushort Mask()
        {
            ushort mask = 0;
            for (var i = 0; i < Failures.Length; i++) if (Failures[i] is not null) mask |= (ushort)(1 << i);
            return mask;
        }
        private AssemblyPublicOutcome Outcome() => new(_unregister, _replacement, Failures, Mask(), _physicalFailure, _observerFailure);

        private void ClaimWindow()
        {
            lock (_parent._gate)
            {
                if (_parent._assemblySealed || _parent._assemblyPublicationFailed ||
                    _parent._assemblyWindow is not null || _parent._slotMutationInProgress ||
                    !_parent._dynamicRegistrations.Contains(Registration) ||
                    !_parent._snapshot.Clusters.TryGetValue(Registration.Slot.Key, out var slot) ||
                    !ReferenceEquals(slot, Registration.Slot))
                    throw new InvalidOperationException("The exact coordinator assembly publication changed before claim.");
                _storage = _parent.CreateMetadataStorage();
                _sourceSnapshot = _parent._snapshot;
                var incoming = Replacement;
                var capacity = checked(_sourceSnapshot.Routes.Count + (incoming?.Contracts.Count ?? 0));
                _storage.ReserveCollection(capacity);
                _storage.ReserveCollection(capacity);
                var routes = new Dictionary<Type, SharpLinkClusterRouteRegistration>(capacity);
                foreach (var pair in _sourceSnapshot.Routes)
                    if (!ReferenceEquals(pair.Value.OwnerAssembly, Registration.Assembly)) routes.Add(pair.Key, pair.Value);
                if (incoming is not null)
                {
                    foreach (var contract in incoming.Contracts)
                    {
                        if (routes.ContainsKey(contract.ContractType) || routes.Values.Any(route => route.ContractId == contract.ContractId))
                            throw new InvalidOperationException("Replacement collides with another exact route owner.");
                        routes.Add(contract.ContractType, new SharpLinkClusterRouteRegistration(contract.ContractType,
                            contract.ContractId, contract.Fingerprint, Registration.Slot, incoming.OwnerAssembly));
                    }
                    _candidateRegistration = new DynamicAssemblyRegistration(Registration.Slot, incoming.OwnerAssembly, incoming, _parent);
                    _candidateRegistration.Capacity = _parent.CreateAssemblyCapacityLocked(incoming.Preparation);
                }
                _candidateSnapshot = MultiClusterSnapshot.Create(_sourceSnapshot.Clusters, routes.ToFrozenDictionary(),
                    _sourceSnapshot.ConfiguredConnectionBudget, _storage, _sourceSnapshot, incoming);
                _parent.AssemblyRetirementForDiagnostics?.Invoke("BeforeWindowClaim", this);
                // All throwing prebuild, including original TCSs, is complete.
                _parent._assemblyWindow = this;
                _claimed = true;
            }
        }

        private void FinalizePublication(ChildAssemblySelectionMailbox selection)
        {
            try
            {
                try { _parent.AssemblyRetirementForDiagnostics?.Invoke("BeforeDispositionFinalize", this); }
                catch (Exception failure) { Record(AssemblyFailureStage.Handoff, failure); }
                lock (_parent._gate)
                {
                    if (!_claimed) return;
                    try
                    {
                        if (!ReferenceEquals(_parent._assemblyWindow, this) ||
                            !ReferenceEquals(_parent._snapshot, _sourceSnapshot))
                            throw new InvalidOperationException("An exact assembly window lost its snapshot authority.");
                        if (selection.Disposition is ChildAssemblyDisposition.SelectedCommitted or ChildAssemblyDisposition.JoinedExistingCommitted)
                        {
                            var index = _parent._dynamicRegistrations.IndexOf(Registration);
                            if (index < 0 || _candidateSnapshot is null)
                                throw new InvalidOperationException("Committed child has no prebuilt coordinator publication.");
                            // Stop seal does not veto a committed child. No Dispose,
                            // callback, allocation or task creation in this swap.
                            _oldSnapshot = _parent._snapshot;
                            Volatile.Write(ref _parent._snapshot, _candidateSnapshot);
                            _candidateSnapshot = null;
                            // Unregister keeps its ContractId reservation until
                            // exact child release. Replacement installs the new
                            // registration, which owns the same ContractId set.
                            if (_candidateRegistration is not null)
                                _parent._dynamicRegistrations[index] = _candidateRegistration;
                            _published = true;
                        }
                    }
                    catch (Exception failure)
                    {
                        _parent._assemblyPublicationFailed = true;
                        Record(AssemblyFailureStage.Swap, failure);
                    }
                    finally
                    {
                        if (ReferenceEquals(_parent._assemblyWindow, this)) _parent._assemblyWindow = null;
                        _claimed = false;
                    }
                }
            }
            finally { PublicationDone.TrySetResult(true); }
        }

        private bool Cleanup(IDisposable? resource, AssemblyFailureStage stage)
        {
            if (resource is null) return true;
            if (resource is DynamicAssemblyRegistration registration)
                registration.DisposalReportedByOuter = true;
            try { resource.Dispose(); return true; }
            catch (Exception failure) { _cleanupCertified = false; Record(stage, failure); return false; }
        }
        private void CleanupPublication()
        {
            if (Cleanup(_oldSnapshot, AssemblyFailureStage.OldSnapshot)) _oldSnapshot = null;
            if (!_published)
            {
                if (Cleanup(_candidateSnapshot, AssemblyFailureStage.CandidateSnapshot)) _candidateSnapshot = null;
                if (Cleanup(_candidateRegistration, AssemblyFailureStage.CandidateRegistration)) _candidateRegistration = null;
            }
            // Preparation release is a single elected phase with complete siblings.
            Exception? preparationFailure = null;
            try { _storage?.Dispose(); _storage = null; }
            catch (Exception failure) { preparationFailure = failure; }
            try { _incomingLease?.Dispose(); _incomingLease = null; }
            catch (Exception failure) { preparationFailure = preparationFailure is null ? failure : new AggregateException(preparationFailure, failure); }
            _sourceSnapshot = null;
            if (preparationFailure is not null) { _cleanupCertified = false; Record(AssemblyFailureStage.Preparation, preparationFailure); }
        }

        internal async Task RunAsync()
        {
            await Installed.Task.ConfigureAwait(false);
            var run = false;
            try
            {
                _parent.AssemblyRetirementForDiagnostics?.Invoke("BeforeTracking", this);
                _parent._frameworkTasks.TrackOwnedStrictPhysicalTail(Worker,
                    "MultiClusterAssemblyRetirement", this, ref Supervision);
                _parent.AssemblyRetirementForDiagnostics?.Invoke("AfterTracking", this);
                run = true;
            }
            catch (Exception failure) { Record(AssemblyFailureStage.Tracking, failure); }
            finally { Start.TrySetResult(run); }
            if (Predecessor is { } previous)
            {
                try { await previous.ConfigureAwait(false); }
                catch (Exception failure) { Record(AssemblyFailureStage.Initialization, OriginalFailure(previous, failure)); run = false; }
                finally { Predecessor = null; }
            }
            ValueTask<SharpLinkAssemblyUnregisterResult> unregister = default;
            ValueTask<SharpLinkAssemblyReplacementResult> replacement = default;
            Exception? returnedProjectionFailure = null;
            Task? logicalOperation = null;
            try
            {
                if (run)
                {
                    _parent.AssemblyRetirementForDiagnostics?.Invoke("BeforeChildInvocation", this);
                    var child = (ISharpLinkPreparedAssemblyClient)Registration.Slot.Client;
                    if (Replacement is null) unregister = child.UnregisterAssemblyAsync(Registration.Assembly, Budget, Mailbox);
                    else replacement = child.ReplacePreparedAssemblyAsync(Registration.Assembly, Replacement, Budget, Mailbox);
                    _childReturned = true;
                }
            }
            catch (Exception failure) { Record(AssemblyFailureStage.Invocation, failure); }
            finally
            {
                Mailbox.FinalizeDisposition();
                if (Mailbox.FinalizationFailure is { } failure) Record(AssemblyFailureStage.Handoff, failure);
                CleanupPublication();
            }
            if (_childReturned)
            {
                try
                {
                    if (Replacement is null)
                    {
                        var original = unregister.AsTask();
                        logicalOperation = original;
                        _unregister = await original.ConfigureAwait(false);
                    }
                    else
                    {
                        var original = replacement.AsTask();
                        logicalOperation = original;
                        _replacement = await original.ConfigureAwait(false);
                        _unregister = new SharpLinkAssemblyUnregisterResult { ReferencesReleased = _replacement.ReferencesReleased,
                            RemainingCalls = _replacement.RemainingCalls, RemainingStreams = _replacement.RemainingStreams };
                    }
                    // A real grace outcome is public data, not release authority.
                    if ((Mailbox.Disposition is ChildAssemblyDisposition.SelectedCommitted or ChildAssemblyDisposition.JoinedExistingCommitted) &&
                        Mailbox.Physical is { IsCompleted: false } && Mask() == 0)
                        Public.TrySetResult(Outcome());
                }
                catch (Exception failure)
                {
                    var graph = logicalOperation is null ? failure : OriginalFailure(logicalOperation, failure);
                    if (Mailbox.InvocationFailure is { } independent)
                        Record(AssemblyFailureStage.Invocation, independent);
                    else if (Mailbox.ReturnedFailureIsSourceProjection && Mailbox.Physical is not null)
                        returnedProjectionFailure = graph;
                    else Record(AssemblyFailureStage.Invocation, graph);
                }
            }
            if (Mailbox.Physical is { } physical)
            {
                try { await physical.ConfigureAwait(false); }
                catch (Exception failure) { _physicalFailure = OriginalFailure(physical, failure); }
            }
            else if (Mailbox.Control?.CreatedDriver is { } partialDriver)
            {
                // A partial construction still owns this real suspended driver.
                Mailbox.Control.Activate();
                var outcome = await partialDriver.Task.ConfigureAwait(false);
                outcome.AppendFailures(ref _physicalFailure);
            }
            if (Mailbox.Observation is { } observation)
            {
                try { _observerFailure = (await observation.ConfigureAwait(false)).Failure; }
                catch (Exception failure) { _observerFailure = OriginalFailure(observation, failure); }
            }
            if (Mailbox.InvocationFailure is { } invocation && Failures[(int)AssemblyFailureStage.Invocation] is null)
                Record(AssemblyFailureStage.Invocation, invocation);
            // A projection claim without any real failing source cannot hide an
            // independent returned operation failure. No exception-type heuristic.
            if (returnedProjectionFailure is not null && _physicalFailure is null && _observerFailure is null &&
                Failures[(int)AssemblyFailureStage.Invocation] is null)
                Record(AssemblyFailureStage.Invocation, returnedProjectionFailure);
            _retry = Mailbox.Disposition == ChildAssemblyDisposition.JoinedUncommittedStage &&
                Mailbox.AbortCertified && Mask() == 0 && _physicalFailure is null && _observerFailure is null;
        }

        internal async Task ObserveAsync()
        {
            await Start.Task.ConfigureAwait(false);
            try { await Worker.ConfigureAwait(false); }
            catch (Exception failure) { Record(AssemblyFailureStage.Shutdown, OriginalFailure(Worker, failure)); }
            if (Supervision is { } receipt)
            {
                try { receipt.Supervisor.CompleteAcceptedRegistration(receipt); }
                catch (Exception failure)
                {
                    Record(AssemblyFailureStage.TrackingCompletion, failure);
                    _cleanupCertified &= receipt.RetainedOwnerRoot is null;
                }
            }
            var childReleased = Mailbox.Control is null || Mailbox.AbortCertified ||
                (Mailbox.HasPhysicalExit && Mailbox.ModuleReleased && Mailbox.AccountingRefunded);
            if (childReleased && _published)
            {
                // Readers use the same gate. Cut the old registration BEFORE
                // metadata disposal outside the gate, so a concurrent loader
                // revalidation cannot read a half-disposed prepared manifest.
                lock (_parent._gate) _parent._dynamicRegistrations.Remove(Registration);
                Cleanup(Registration, AssemblyFailureStage.OldRegistration);
            }
            if (childReleased && _cleanupCertified && !_parent._assemblyPublicationFailed)
            {
                try
                {
                    _parent.AssemblyRetirementForDiagnostics?.Invoke("BeforeFinalRefund", this);
                    Capacity.Dispose();
                }
                catch (Exception failure) { Record(AssemblyFailureStage.TokenRefund, failure); }
                Certified = Capacity.Refunded;
            }
            lock (_parent._gate)
            {
                if (Certified && !_parent._assemblySealed)
                {
                    if (_retry && Mask() == 0)
                    {
                        try
                        {
                            _parent.StartAssemblyOwnerLocked(Registration, Budget, null, this);
                            // No fallible tail, cleanup, callback or successor
                            // await after ownership transfer. Its Worker joins us.
                            return;
                        }
                        catch (Exception failure) { Record(AssemblyFailureStage.Initialization, failure); }
                    }
                    _parent.ReleaseAssemblyOwnerLocked(this);
                }
            }
            if (!Certified && Mask() == 0 && _physicalFailure is null && _observerFailure is null)
                Record(AssemblyFailureStage.TokenRefund, new InvalidOperationException("Assembly ownership ended without a physical release certificate."));
            Public.TrySetResult(Outcome());
        }
    }

    private static Exception OriginalFailure(Task original, Exception awaited)
    {
        if (original.Exception is not { } graph) return PhysicalTaskFailureException.GetRaw(awaited);
        return graph.InnerExceptions.Count == 1 ? PhysicalTaskFailureException.GetRaw(graph.InnerExceptions[0]) : graph;
    }
    private void ReleaseAssemblyOwnerLocked(CoordinatorAssemblyRetirement owner)
    {
        CoordinatorAssemblyRetirement? previous = null;
        for (var current = _assemblyOwners; current is not null; current = current.Next)
        {
            if (!ReferenceEquals(current, owner)) { previous = current; continue; }
            if (previous is null) _assemblyOwners = current.Next;
            else previous.Next = current.Next;
            current.Next = null;
            _assemblyOwnerCount--;
            _unregisterOperations.Remove(owner.Registration);
            _drainingRegistrations.Remove(owner.Registration);
            if (owner.Replacement is not null) _activeAssemblyReplacements--;
            return;
        }
    }
    private static void ThrowAssemblyOutcome(AssemblyPublicOutcome result)
    {
        // This array/aggregate belongs to the public caller's projection, not an
        // internal retained history or Stop traversal inventory.
        var count = (result.PhysicalFailure is null ? 0 : 1) + (result.ObserverFailure is null ? 0 : 1);
        for (var i = 0; i < 16; i++) if ((result.FailureMask & (1 << i)) != 0) count++;
        if (count == 0) return;
        var errors = new Exception[count];
        var index = 0;
        if (result.PhysicalFailure is { } physical) errors[index++] = physical;
        if (result.ObserverFailure is { } observer) errors[index++] = observer;
        for (var i = 0; i < 16; i++) if ((result.FailureMask & (1 << i)) != 0) errors[index++] = result.IndependentFailures[i]!;
        if (errors.Length == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        throw new AggregateException(errors);
    }
    private static async ValueTask<SharpLinkAssemblyUnregisterResult> WaitAssemblyUnregisterAsync(
        CoordinatorAssemblyRetirement owner, CancellationToken token)
    {
        // Coalesced callers keep this same pending logical Task across a bounded
        // physical-stage handoff; callers never independently elect successors.
        var original = owner.Public.Task;
        var result = await (token.CanBeCanceled ? original.WaitAsync(token) : original).ConfigureAwait(false);
        ThrowAssemblyOutcome(result);
        return result.Unregister;
    }
    private static async ValueTask<SharpLinkAssemblyReplacementResult> WaitAssemblyReplacementAsync(
        CoordinatorAssemblyRetirement owner, CancellationToken token)
    {
        var result = await (token.CanBeCanceled ? owner.Public.Task.WaitAsync(token) : owner.Public.Task).ConfigureAwait(false);
        ThrowAssemblyOutcome(result);
        return result.Replacement;
    }
}
