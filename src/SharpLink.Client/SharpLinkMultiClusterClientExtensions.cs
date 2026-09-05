using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

/// <summary>Reports the bounded cleanup result of a removed multi-cluster slot.</summary>
public readonly record struct SharpLinkClusterRemovalResult
{
    /// <summary>Gets whether the slot and its routes were removed from the public snapshot.</summary>
    public bool Succeeded { get; init; }

    /// <summary>Gets whether the retired child released its owned resources before the graceful timeout elapsed.</summary>
    public bool ReferencesReleased { get; init; }

    /// <summary>Gets whether forced shutdown continued in the background after the graceful timeout elapsed.</summary>
    public bool ForcedStop { get; init; }
}

/// <summary>Adds runtime lifecycle operations to a SharpLink multi-cluster client.</summary>
public static class SharpLinkMultiClusterClientExtensions
{
    /// <summary>Builds and atomically adds a cluster slot while the coordinator is running.</summary>
    /// <remarks>Cancellation before publication rolls back the candidate and leaves the public snapshot unchanged.</remarks>
    public static ValueTask AddClusterAsync(
        this ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure,
        Action<SharpLinkMultiClusterSlotOptions>? configureSlot = null,
        CancellationToken cancellationToken = default)
        => AddClusterCoreAsync(
            client,
            cluster,
            configure,
            configureSlot,
            cancellationToken,
            GlobalCatalogManifestSource.Instance,
            GlobalCatalogClusterRouteSource.Instance);

    internal static ValueTask AddClusterAsync(
        this ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure,
        Action<SharpLinkMultiClusterSlotOptions>? configureSlot,
        CancellationToken cancellationToken,
        IGeneratedManifestSource manifestSource,
        IGeneratedClusterRouteSource routeSource)
        => AddClusterCoreAsync(
            client,
            cluster,
            configure,
            configureSlot,
            cancellationToken,
            manifestSource,
            routeSource);

    private static async ValueTask AddClusterCoreAsync(
        ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure,
        Action<SharpLinkMultiClusterSlotOptions>? configureSlot,
        CancellationToken cancellationToken,
        IGeneratedManifestSource manifestSource,
        IGeneratedClusterRouteSource routeSource)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentNullException.ThrowIfNull(manifestSource);
        ArgumentNullException.ThrowIfNull(routeSource);
        var builder = SharpClientBuilder.Create();
        try
        {
            var control = GetLifecycleControl(client);
            control.ConfigureChildBuilder(builder);
            configure(builder);
            var slotOptions = new SharpLinkMultiClusterSlotOptions();
            configureSlot?.Invoke(slotOptions);
            await control.AddClusterAsync(
                cluster,
                builder,
                slotOptions.AllowDynamicContracts,
                cancellationToken,
                manifestSource,
                routeSource).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RethrowAfterBuilderCleanup(exception, builder);
        }
    }

    /// <summary>
    /// Builds a ready replacement, atomically switches future proxy creation, and retires the old slot.
    /// Existing proxies remain bound to the old child and reject new calls after that child stops.
    /// </summary>
    /// <remarks>
    /// Cancellation before publication rolls back the candidate. After publication it only cancels the caller's
    /// wait; coordinator-owned retirement continues in the background.
    /// </remarks>
    public static async ValueTask ReplaceClusterAsync(
        this ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster,
        Action<SharpClientBuilder> configure,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(configure);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        var builder = SharpClientBuilder.Create();
        try
        {
            var control = GetLifecycleControl(client);
            control.ConfigureChildBuilder(builder);
            configure(builder);
            await control.ReplaceClusterAsync(
                cluster,
                builder,
                gracefulTimeout,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            RethrowAfterBuilderCleanup(exception, builder);
        }
    }

    /// <summary>Atomically removes a cluster slot and starts bounded cleanup of its retired child.</summary>
    /// <remarks>
    /// Cancellation after the slot is unpublished only cancels the caller's wait; coordinator-owned cleanup
    /// continues in the background.
    /// </remarks>
    public static ValueTask<SharpLinkClusterRemovalResult> RemoveClusterAsync(
        this ISharpLinkMultiClusterClient client,
        SharpLinkClusterKey cluster,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfLessThan(gracefulTimeout, TimeSpan.Zero);
        return GetLifecycleControl(client).RemoveClusterAsync(
            cluster,
            gracefulTimeout,
            cancellationToken);
    }

    private static ISharpLinkMultiClusterLifecycleControl GetLifecycleControl(
        ISharpLinkMultiClusterClient client)
        => client as ISharpLinkMultiClusterLifecycleControl ??
           throw new NotSupportedException(
               "This ISharpLinkMultiClusterClient implementation does not support runtime cluster lifecycle operations.");

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void RethrowAfterBuilderCleanup(Exception exception, SharpClientBuilder builder)
    {
        try
        {
            builder.DisposeUnbuiltResources();
        }
        catch (Exception cleanupException)
        {
            throw new AggregateException(exception, cleanupException);
        }

        ExceptionDispatchInfo.Capture(exception).Throw();
        throw new UnreachableException();
    }
}

internal interface ISharpLinkMultiClusterLifecycleControl
{
    void ConfigureChildBuilder(SharpClientBuilder builder);

    ValueTask AddClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        bool allowDynamicContracts,
        CancellationToken cancellationToken,
        IGeneratedManifestSource manifestSource,
        IGeneratedClusterRouteSource routeSource);

    ValueTask ReplaceClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken);

    ValueTask<SharpLinkClusterRemovalResult> RemoveClusterAsync(
        SharpLinkClusterKey cluster,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken);
}
