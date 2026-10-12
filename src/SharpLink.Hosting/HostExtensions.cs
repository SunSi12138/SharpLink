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

            AddHealthCheckOnce<SharpLinkServerHealthCheck>(services, "sharplink_server");
            services.AddHostedService<SharpLinkServerHostedService>();
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
            AddHealthCheckOnce<SharpLinkRemoteHealthCheck>(services, "sharplink_remote");
            services.AddHostedService<SharpLinkClientHostedService>();
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

    private static void AddHealthCheckOnce<THealthCheck>(IServiceCollection services, string name)
        where THealthCheck : class, IHealthCheck
    {
        // Hosted services may already exist independently of their readiness checks.
        // AddCheck itself does not deduplicate registrations, so track each role separately.
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(HealthCheckRegistration<THealthCheck>))
                return;
        }

        services.AddHealthChecks().AddCheck<THealthCheck>(name, tags: ["ready"]);
        services.AddSingleton(new HealthCheckRegistration<THealthCheck>());
    }

    private sealed class HealthCheckRegistration<THealthCheck> where THealthCheck : class, IHealthCheck;
}
