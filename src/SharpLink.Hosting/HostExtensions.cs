using SharpLink.Runtime;

namespace SharpLink.Hosting;

/// <summary>Registers SharpLink servers, clients, health checks, and lifecycle services with a generic host.</summary>
public static class HostExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>Adds one hosted SharpLink server and its readiness health check.</summary>
        /// <param name="configure">Optionally configures the registered server builder.</param>
        /// <returns>The registered builder for additional configuration.</returns>
        public SharpLinkServerBuilder AddSharpLinkServer(Action<SharpLinkServerBuilder>? configure = null)
        {
            var builder = ConfigureRegisteredBuilder(
                services, SharpLinkServerBuilder.Create, configure);
            services.TryAddSingleton<SharpLinkServerReadiness>();
            services.TryAddSingleton<ISharpLinkServerReadiness>(static provider =>
                provider.GetRequiredService<SharpLinkServerReadiness>());
            if (builder.Transport is IAnonymousPipeAllocator anonymousPipeAllocator)
            {
                services.TryAddSingleton<IAnonymousPipeAllocatorAccessor>(
                    new AnonymousPipeAllocatorAccessor { AnonymousPipeAllocator = anonymousPipeAllocator });
            }

            if (!HasHostedService<SharpLinkServerHostedService>(services))
            {
                services.AddHealthChecks()
                    .AddCheck<SharpLinkServerHealthCheck>("sharplink_server", tags: ["ready"]);
                services.AddHostedService<SharpLinkServerHostedService>();
            }
            return builder;
        }

        /// <summary>Adds one hosted SharpLink client and its remote readiness health check.</summary>
        /// <param name="configure">Optionally configures the registered client builder.</param>
        /// <returns>The registered builder for additional configuration.</returns>
        public SharpClientBuilder AddSharpLinkClient(Action<SharpClientBuilder>? configure = null)
        {
            var builder = ConfigureRegisteredBuilder(
                services, SharpClientBuilder.Create, configure);
            services.TryAddSingleton<SharpLinkClientAccessor>();
            services.TryAddSingleton<ISharpLinkClientAccessor>(sp => sp.GetRequiredService<SharpLinkClientAccessor>());
            if (!HasHostedService<SharpLinkClientHostedService>(services))
            {
                services.AddHealthChecks()
                    .AddCheck<SharpLinkRemoteHealthCheck>("sharplink_remote", tags: ["ready"]);
                services.AddHostedService<SharpLinkClientHostedService>();
            }
            return builder;
        }

        /// <summary>Adds one hosted multi-cluster coordinator without exposing individual child clients to DI.</summary>
        public SharpLinkMultiClusterClientBuilder AddSharpLinkMultiClusterClient(
            Action<SharpLinkMultiClusterClientBuilder>? configure = null)
        {
            var builder = ConfigureRegisteredBuilder(
                services, SharpLinkMultiClusterClientBuilder.Create, configure);
            services.TryAddSingleton<SharpLinkMultiClusterClientAccessor>();
            services.TryAddSingleton<ISharpLinkMultiClusterClientAccessor>(static provider =>
                provider.GetRequiredService<SharpLinkMultiClusterClientAccessor>());
            if (!HasHostedService<SharpLinkMultiClusterClientHostedService>(services))
                services.AddHostedService<SharpLinkMultiClusterClientHostedService>();
            return builder;
        }
    }

    /// <summary>
    /// Never return a fresh, unregistered builder from a repeated AddSharpLink* call:
    /// TryAddSingleton would otherwise retain an older instance and silently discard
    /// any configure callback applied to this one.
    /// </summary>
    private static TBuilder ConfigureRegisteredBuilder<TBuilder>(
        IServiceCollection services,
        Func<TBuilder> create,
        Action<TBuilder>? configure)
        where TBuilder : class
    {
        ServiceDescriptor? registered = null;
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService || descriptor.ServiceType != typeof(TBuilder))
                continue;
            if (registered is not null)
            {
                throw new InvalidOperationException(
                    $"More than one {typeof(TBuilder).Name} registration exists. " +
                    "A SharpLink host requires one unambiguous builder instance.");
            }
            registered = descriptor;
        }

        if (registered is not null)
        {
            if (registered.ImplementationInstance is not TBuilder existing)
            {
                throw new InvalidOperationException(
                    $"{typeof(TBuilder).Name} was registered by type or factory. " +
                    "AddSharpLink* cannot configure a builder that is not an existing singleton instance.");
            }

            configure?.Invoke(existing);
            return existing;
        }

        var builder = create();
        configure?.Invoke(builder);
        services.AddSingleton(builder);
        return builder;
    }

    private static bool HasHostedService<THostedService>(IServiceCollection services)
        where THostedService : class, IHostedService
    {
        foreach (var descriptor in services)
        {
            if (descriptor.IsKeyedService || descriptor.ServiceType != typeof(IHostedService))
                continue;
            if (descriptor.ImplementationType == typeof(THostedService) ||
                descriptor.ImplementationInstance is THostedService)
            {
                return true;
            }
        }
        return false;
    }
}
