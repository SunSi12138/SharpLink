using System.Runtime.ExceptionServices;

namespace SharpLink.Client;

/// <summary>Adds runtime lifecycle operations to a SharpLink multi-cluster client.</summary>
public static class SharpLinkMultiClusterClientExtensions
{
    /// <summary>Builds and atomically adds a cluster slot to the local coordinator.</summary>
    /// <remarks>
    /// When the coordinator is running, a successful <see cref="SharpLinkClusterAddResult"/> means the child runtime
    /// and routes are published and coordinator-owned; it does not guarantee that the remote cluster is ready. Call
    /// <see cref="ISharpLinkMultiClusterClient.WaitForReadyAsync(SharpLinkClusterKey, CancellationToken)"/>
    /// before issuing work that requires immediate remote availability. Expected control-plane rejection is reported
    /// through <see cref="SharpLinkClusterAddResult.FailureCode"/>. Programmer errors, cancellation, configuration
    /// failures, and unexpected runtime failures remain exceptions.
    /// </remarks>
    public static ValueTask<SharpLinkClusterAddResult> AddClusterAsync(
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

    internal static ValueTask<SharpLinkClusterAddResult> AddClusterAsync(
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

    private static async ValueTask<SharpLinkClusterAddResult> AddClusterCoreAsync(
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
            var result = await control.AddClusterAsync(
                cluster,
                builder,
                slotOptions.AllowDynamicContracts,
                cancellationToken,
                manifestSource,
                routeSource).ConfigureAwait(false);
            if (!result.Succeeded)
                builder.DisposeUnbuiltResources();
            return result;
        }
        catch (Exception exception)
        {
            RethrowAfterBuilderCleanup(exception, builder);
            throw new UnreachableException();
        }
    }

    /// <summary>
    /// Builds a ready replacement, atomically switches future proxy creation, and retires the old slot.
    /// Existing proxies remain bound to the old child and reject new calls after that child stops.
    /// </summary>
    /// <remarks>
    /// Expected rejection before publication is reported through
    /// <see cref="SharpLinkClusterReplacementResult.FailureCode"/> with
    /// <see cref="SharpLinkClusterReplacementResult.Published"/> equal to <see langword="false"/>.
    /// After publication, bounded retirement is reported independently through
    /// <see cref="SharpLinkClusterReplacementResult.ReferencesReleased"/> and
    /// <see cref="SharpLinkClusterReplacementResult.ForcedStop"/>. Caller cancellation remains exceptional and
    /// never rolls back a committed publication.
    /// </remarks>
    public static async ValueTask<SharpLinkClusterReplacementResult> ReplaceClusterAsync(
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
            var result = await control.ReplaceClusterAsync(
                cluster,
                builder,
                gracefulTimeout,
                cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded)
                builder.DisposeUnbuiltResources();
            return result;
        }
        catch (Exception exception)
        {
            RethrowAfterBuilderCleanup(exception, builder);
            throw new UnreachableException();
        }
    }

    /// <summary>Atomically removes a cluster slot and starts bounded cleanup of its retired child.</summary>
    /// <remarks>
    /// Expected rejection before unpublication is reported through
    /// <see cref="SharpLinkClusterRemovalResult.FailureCode"/>. After successful unpublication, bounded cleanup is
    /// reported through <see cref="SharpLinkClusterRemovalResult.ReferencesReleased"/> and
    /// <see cref="SharpLinkClusterRemovalResult.ForcedStop"/>. Cancellation after unpublication only cancels the
    /// caller's wait; coordinator-owned cleanup continues in the background.
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

    ValueTask<SharpLinkClusterAddResult> AddClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        bool allowDynamicContracts,
        CancellationToken cancellationToken,
        IGeneratedManifestSource manifestSource,
        IGeneratedClusterRouteSource routeSource);

    ValueTask<SharpLinkClusterReplacementResult> ReplaceClusterAsync(
        SharpLinkClusterKey cluster,
        SharpClientBuilder builder,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken);

    ValueTask<SharpLinkClusterRemovalResult> RemoveClusterAsync(
        SharpLinkClusterKey cluster,
        TimeSpan gracefulTimeout,
        CancellationToken cancellationToken);
}
