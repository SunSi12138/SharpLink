namespace SharpLink.Runtime;

/// <summary>Describes whether a build resource is owned by the framework or its caller.</summary>
internal enum SynchronousBuildResourceOwnership
{
    FrameworkOwned,
    CallerOwned
}

/// <summary>Names a resource registered while a synchronous builder materializes its final runtime.</summary>
internal readonly record struct SynchronousBuildResourceMetadata
{
    internal SynchronousBuildResourceMetadata(
        string name,
        SynchronousBuildResourceOwnership ownership)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!Enum.IsDefined(ownership))
            throw new ArgumentOutOfRangeException(nameof(ownership));

        Name = name;
        Ownership = ownership;
    }

    internal string Name { get; }

    internal SynchronousBuildResourceOwnership Ownership { get; }

    internal static SynchronousBuildResourceMetadata FrameworkOwned(string name)
        => new(name, SynchronousBuildResourceOwnership.FrameworkOwned);

    internal static SynchronousBuildResourceMetadata CallerOwned(string name)
        => new(name, SynchronousBuildResourceOwnership.CallerOwned);
}

/// <summary>
/// Tracks framework-owned resources during synchronous builder materialization until their final runtime is created.
/// </summary>
/// <remarks>
/// This is a construction-only cold-path primitive. It does not participate in runtime start, stop, or drain paths.
/// </remarks>
internal sealed class SynchronousBuildTransaction : IDisposable
{
    private readonly List<Entry> _entries = [];
    private readonly HashSet<object> _resources = new(ReferenceEqualityComparer.Instance);
    private State _state;

    /// <summary>Registers one resource and its cleanup action in ownership-transfer order.</summary>
    internal T Own<T>(
        T resource,
        Action<T>? cleanup,
        SynchronousBuildResourceMetadata metadata)
        where T : class
    {
        EnsureActive("register a resource");
        ArgumentNullException.ThrowIfNull(resource);
        ValidateMetadata(metadata, cleanup);
        if (!_resources.Add(resource))
        {
            throw new InvalidOperationException(
                $"Resource '{metadata.Name}' was already registered by this build transaction.");
        }

        _entries.Add(new Entry(resource, cleanup is null ? null : () => cleanup(resource), metadata));
        return resource;
    }

    /// <summary>Registers a sequence one item at a time so each resource receives identity validation.</summary>
    internal void OwnRange<T>(
        IEnumerable<T> resources,
        Action<T>? cleanup,
        SynchronousBuildResourceMetadata metadata)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(resources);
        foreach (var resource in resources)
            Own(resource, cleanup, metadata);
    }

    /// <summary>Transfers every registered framework-owned resource to the completed runtime.</summary>
    internal void Commit()
    {
        EnsureActive("commit");
        _entries.Clear();
        _resources.Clear();
        _state = State.Committed;
    }

    /// <summary>Alias for <see cref="Commit"/> at ownership-transfer call sites.</summary>
    internal void Transfer() => Commit();

    /// <summary>Releases registered framework-owned resources in reverse registration order.</summary>
    internal void Rollback()
    {
        var cleanupFailures = Cleanup();
        ThrowCleanupFailures(cleanupFailures);
    }

    /// <summary>
    /// Releases registered framework-owned resources in reverse registration order, preserving the primary failure.
    /// </summary>
    [DoesNotReturn]
    internal void Rollback(Exception primaryException)
    {
        ArgumentNullException.ThrowIfNull(primaryException);
        ThrowAfterRollback(primaryException, Cleanup());
    }

    /// <summary>
    /// Rolls back an active transaction. Disposal after a terminal commit or rollback is intentionally a no-op.
    /// </summary>
    public void Dispose()
    {
        switch (_state)
        {
            case State.Active:
                Rollback();
                return;
            case State.Committed:
            case State.RolledBack:
                return;
            case State.RollingBack:
                throw new InvalidOperationException("Cannot dispose a build transaction while it is rolling back.");
            default:
                throw new UnreachableException();
        }
    }

    private static void ValidateMetadata<T>(
        SynchronousBuildResourceMetadata metadata,
        Action<T>? cleanup)
        where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(metadata.Name);
        if (!Enum.IsDefined(metadata.Ownership))
            throw new ArgumentOutOfRangeException(nameof(metadata));

        if (metadata.Ownership == SynchronousBuildResourceOwnership.FrameworkOwned && cleanup is null)
        {
            throw new ArgumentException(
                $"Framework-owned resource '{metadata.Name}' requires a cleanup action.",
                nameof(cleanup));
        }
        if (metadata.Ownership == SynchronousBuildResourceOwnership.CallerOwned && cleanup is not null)
        {
            throw new ArgumentException(
                $"Caller-owned resource '{metadata.Name}' cannot provide a cleanup action.",
                nameof(cleanup));
        }
    }

    private List<Exception>? Cleanup()
    {
        EnsureActive("roll back");
        _state = State.RollingBack;
        List<Exception>? cleanupFailures = null;
        try
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                var cleanup = _entries[index].Cleanup;
                if (cleanup is null)
                    continue;

                try
                {
                    cleanup();
                }
                catch (Exception cleanupException)
                {
                    (cleanupFailures ??= []).Add(cleanupException);
                }
            }
        }
        finally
        {
            _entries.Clear();
            _resources.Clear();
            _state = State.RolledBack;
        }

        return cleanupFailures;
    }

    [DoesNotReturn]
    private static void ThrowAfterRollback(Exception primaryException, List<Exception>? cleanupFailures)
    {
        if (cleanupFailures is null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(primaryException).Throw();
        }

        cleanupFailures!.Insert(0, primaryException);
        throw new AggregateException(cleanupFailures);
    }

    private static void ThrowCleanupFailures(List<Exception>? cleanupFailures)
    {
        if (cleanupFailures is null)
            return;
        if (cleanupFailures.Count == 1)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFailures[0]).Throw();
        throw new AggregateException(cleanupFailures);
    }

    private void EnsureActive(string operation)
    {
        if (_state == State.Active)
            return;
        throw new InvalidOperationException(
            $"Cannot {operation} because the build transaction is {_state.ToString().ToLowerInvariant()}.");
    }

    private sealed record Entry(
        object Resource,
        Action? Cleanup,
        SynchronousBuildResourceMetadata Metadata);

    private enum State : byte
    {
        Active,
        RollingBack,
        Committed,
        RolledBack
    }
}
