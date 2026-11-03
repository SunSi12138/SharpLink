namespace SharpLink.Client;

internal sealed partial class SharpLinkMultiClusterClient
{
    private AssemblyStopFailure _assemblyStopError = null!;
    private MultiClusterSnapshot? _failedPublishedStopSnapshot;
    private MultiClusterSnapshot? _failedCapturedStopSnapshot;
    private enum AssemblyStopStage : byte
    {
        ShutdownCancellation, AdmittedMetadataJoin, WindowPublicationJoin,
        SupervisorDrain, PublishedSnapshotDispose, CapturedSnapshotLeaseDispose,
        CoordinatorMetadataDispose, ShutdownTokenDispose,
        ChildStop, ChildMetadataRelease, OuterStage, RegistrationCleanup,
        FailedAdmission, UncertifiedOwner, RegistrationSnapshotCleanup, RegistrationDisposal
    }
    private readonly record struct AssemblyStopOccurrence(AssemblyStopStage Stage,
        object Owner, Task? Original, Exception? Failure, int OuterStageIndex = -1);

    // Prepaid summary references the exact sealed roots; it does not flatten or
    // duplicate the child Stop's physical source graphs into a growing list.
    private sealed class AssemblyStopFailure(SharpLinkMultiClusterClient owner)
        : Exception("Coordinator shutdown retained independent failures or uncertified assembly ownership.")
    {
        // Enumeration is a diagnostic/public observer operation after sealing,
        // not a Stop-side allocation or a new physical ownership task.
        internal IEnumerable<AssemblyStopOccurrence> Occurrences => owner.EnumerateAssemblyStopOccurrences();
    }
    private IEnumerable<AssemblyStopOccurrence> EnumerateAssemblyStopOccurrences()
    {
        for (var i = 0; i < _assemblyStopFailures.Length; i++)
            if (_assemblyStopFailures[i] is { } failure)
                yield return new((AssemblyStopStage)i,
                    i == 4 && _failedPublishedStopSnapshot is { } published ? published :
                    i == 5 && _failedCapturedStopSnapshot is { } captured ? captured : (object)this, null, failure);
        for (var i = 0; i < _assemblyStopEntries.Length; i++)
        {
            var entry = _assemblyStopEntries[i];
            if (entry.Slot is not null && entry.Failure is not null)
                yield return new(entry.MetadataReleaseAttempted ? AssemblyStopStage.ChildMetadataRelease : AssemblyStopStage.ChildStop,
                    entry.Slot, entry.Original, entry.Failure);
        }
        for (var current = _assemblyOwners; current is not null; current = current.Next)
        {
            for (var i = 0; i < current.Failures.Length; i++)
                if (current.Failures[i] is { } failure)
                    yield return new(AssemblyStopStage.OuterStage, current, current.Observer, failure, i);
            if (!current.Certified) yield return new(AssemblyStopStage.UncertifiedOwner, current, current.Observer, null);
        }
        foreach (var registration in _dynamicRegistrations)
            if (registration.StopFailure is { } failure)
                yield return new(AssemblyStopStage.RegistrationCleanup, registration, null, failure);
        for (var registration = _failedAssemblyRegistrations; registration is not null; registration = registration.FailedNext)
        {
            if (registration.SnapshotFailure is { } snapshot)
                yield return new(AssemblyStopStage.RegistrationSnapshotCleanup,
                    registration.FailedSnapshot!, null, snapshot);
            if (!registration.DisposalReportedByOuter && registration.StopFailure is null &&
                registration.DisposalFailure is { } disposal)
                yield return new(AssemblyStopStage.RegistrationDisposal, registration, null, disposal);
        }
        for (var capacity = _failedAssemblyCapacities; capacity is not null; capacity = capacity.FailedNext)
            yield return new(AssemblyStopStage.FailedAdmission, capacity, null, capacity.Failure);
    }

    private async Task StopAssemblyCoordinatorAsync()
    {
        // Called under the existing coordinator gate. No new D2 reserve follows.
        var snapshotLease = _snapshot.Retain();
        _assemblySealed = true;
        for (var owner = _assemblyOwners; owner is not null; owner = owner.Next) owner.StopSelected = true;
        var publication = _assemblyWindow?.PublicationDone.Task;
        Volatile.Write(ref _state, (int)SharpLinkMultiClusterState.Draining);
        _frameworkTasks.Seal();
        var metadataJoined = _metadataDomain.SealAndJoinAsync();
        await Task.Yield();
        try { await _shutdown.CancelAsync().ConfigureAwait(false); }
        catch (Exception failure) { _assemblyStopFailures[0] = failure; }
        try { await metadataJoined.ConfigureAwait(false); }
        catch (Exception failure) { _assemblyStopFailures[1] = OriginalFailure(metadataJoined, failure); }
        if (publication is not null)
        {
            try { await publication.ConfigureAwait(false); }
            catch (Exception failure) { _assemblyStopFailures[2] = OriginalFailure(publication, failure); }
        }
        await _mutationGate.WaitAsync().ConfigureAwait(false);
        var failed = _assemblyPublicationFailed || _failedAssemblyCapacities is not null || _failedAssemblyRegistrations is not null;
        for (var i = 0; i < _assemblyStopFailures.Length; i++) if (_assemblyStopFailures[i] is not null) failed = true;
        try
        {
            var slots = snapshotLease.Slots;
            // Start every original child Stop once before joining any of them.
            // A synchronous invocation failure has no invented physical task.
            for (var index = 0; index < slots.Length; index++)
            {
                ref var entry = ref _assemblyStopEntries[index];
                entry.Slot = slots[index];
                try { entry.Original = slots[index].Client.StopAsync().AsTask(); }
                catch (Exception failure) { entry.Failure = failure; }
            }
            for (var index = 0; index < slots.Length; index++)
            {
                var original = _assemblyStopEntries[index].Original;
                if (original is null) { failed = true; continue; }
                try { await original.ConfigureAwait(false); }
                catch (Exception failure) { _assemblyStopEntries[index].Failure = OriginalFailure(original, failure); failed = true; }
            }
            // Child Stop reports child physical/observer source occurrences. Outer
            // typed workers report only their OWN independent stage occurrences.
            for (var owner = _assemblyOwners; owner is not null; owner = owner.Next)
            {
                try { await owner.Observer.ConfigureAwait(false); }
                catch (Exception failure) { owner.Record(AssemblyFailureStage.Shutdown, OriginalFailure(owner.Observer, failure)); }
                if (!owner.Certified) failed = true;
                for (var i = 0; i < owner.Failures.Length; i++) if (owner.Failures[i] is not null) failed = true;
            }
            try { await _frameworkTasks.DrainAsync().ConfigureAwait(false); }
            catch (Exception failure) { _assemblyStopFailures[3] = failure; failed = true; }

            // Unknown child exit forbids its metadata release even if a particular
            // D2 module independently released. All selected roots stay discoverable.
            for (var index = 0; index < slots.Length; index++)
            {
                ref var entry = ref _assemblyStopEntries[index];
                if (entry.Failure is not null || entry.Original is not { IsCompletedSuccessfully: true }) continue;
                var certified = true;
                for (var owner = _assemblyOwners; owner is not null; owner = owner.Next)
                    if (ReferenceEquals(owner.Registration.Slot, entry.Slot) && !owner.Certified) certified = false;
                for (var registration = _failedAssemblyRegistrations; registration is not null; registration = registration.FailedNext)
                    if (ReferenceEquals(registration.Slot, entry.Slot)) certified = false;
                if (!certified) continue;
                entry.MetadataReleaseAttempted = true;
                try { entry.Slot!.ReleaseMetadata(); }
                catch (Exception failure) { entry.Failure = failure; failed = true; }
            }
            if (!failed)
            {
                // No user cleanup under the publication gate. Failed registration
                // disposal remains in its own prepaid registration field.
                foreach (var registration in _dynamicRegistrations)
                {
                    try { registration.Dispose(); }
                    catch (Exception failure) { registration.StopFailure = failure; failed = true; }
                }
            }
            if (!failed)
            {
                MultiClusterSnapshot old;
                lock (_gate)
                {
                    old = _snapshot;
                    Volatile.Write(ref _snapshot, MultiClusterSnapshot.Empty);
                    _dynamicRegistrations.Clear();
                    _drainingRegistrations.Clear();
                    _unregisterOperations.Clear();
                    _assemblyOwners = null;
                    _assemblyOwnerCount = 0;
                    _activeAssemblyReplacements = 0;
                    _transitionConnectionBudget = 0;
                }
                _failedPublishedStopSnapshot = old;
                try { old.Dispose(); _failedPublishedStopSnapshot = null; }
                catch (Exception failure) { _assemblyStopFailures[4] = failure; failed = true; }
            }
        }
        finally { _mutationGate.Release(); }
        _failedCapturedStopSnapshot = snapshotLease;
        try { snapshotLease.Dispose(); _failedCapturedStopSnapshot = null; }
        catch (Exception failure) { _assemblyStopFailures[5] = failure; failed = true; }
        for (var i = 0; i < _assemblyStopFailures.Length; i++) if (_assemblyStopFailures[i] is not null) failed = true;
        if (!failed)
        {
            try { _metadataOwner?.Dispose(); _metadataOwner = null; }
            catch (Exception failure) { _assemblyStopFailures[6] = failure; failed = true; }
        }
        try { _shutdown.Dispose(); }
        catch (Exception failure) { _assemblyStopFailures[7] = failure; failed = true; }
        Volatile.Write(ref _state, (int)(failed ? SharpLinkMultiClusterState.Faulted : SharpLinkMultiClusterState.Stopped));
        if (failed) throw _assemblyStopError;
    }
}
