using SharpLink.Runtime;

namespace SharpLink.Hosting;

public static class HostExtensions
{
    extension(IServiceCollection services)
    {
        public SharpLinkServerBuilder AddSharpLinkServer(Action<SharpLinkServerBuilder>? configure=null)
        {
            var builder = SharpLinkServerBuilder.Create();
            configure?.Invoke(builder);
            services.TryAddSingleton(builder);
            services.TryAddSingleton<SharpLinkServerReadiness>();
            services.TryAddSingleton<ISharpLinkServerReadiness>(static provider =>
                provider.GetRequiredService<SharpLinkServerReadiness>());
            services.AddHealthChecks()
                .AddCheck<SharpLinkServerHealthCheck>("sharplink_server", tags: ["ready"]);
            if (builder.Transport is IAnonymousPipeAllocator anonymousPipeAllocator)
                services.AddSingleton<IAnonymousPipeAllocatorAccessor>(new AnonymousPipeAllocatorAccessor{AnonymousPipeAllocator = anonymousPipeAllocator});

            services.AddHostedService<SharpLinkServerHostedService>();
            return builder;
        }

        public SharpClientBuilder AddSharpLinkClient(Action<SharpClientBuilder>? configure=null)
        {
            var builder = SharpClientBuilder.Create();
            configure?.Invoke(builder);
            services.TryAddSingleton(builder);
            services.TryAddSingleton<SharpLinkClientAccessor>();
            services.TryAddSingleton<ISharpLinkClientAccessor>(sp => sp.GetRequiredService<SharpLinkClientAccessor>());
            services.AddHealthChecks()
                .AddCheck<SharpLinkRemoteHealthCheck>("sharplink_remote", tags: ["ready"]);
            services.AddHostedService<SharpLinkClientHostedService>();
            return builder;
        }

        /// <summary>Adds one hosted multi-cluster coordinator without exposing individual child clients to DI.</summary>
        public SharpLinkMultiClusterClientBuilder AddSharpLinkMultiClusterClient(
            Action<SharpLinkMultiClusterClientBuilder>? configure = null)
        {
            var builder = SharpLinkMultiClusterClientBuilder.Create();
            configure?.Invoke(builder);
            services.TryAddSingleton(builder);
            services.TryAddSingleton<SharpLinkMultiClusterClientAccessor>();
            services.TryAddSingleton<ISharpLinkMultiClusterClientAccessor>(static provider =>
                provider.GetRequiredService<SharpLinkMultiClusterClientAccessor>());
            services.AddHostedService<SharpLinkMultiClusterClientHostedService>();
            return builder;
        }
    }
}
