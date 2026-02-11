namespace SharpLink.Hosting;

public static class HostExtensions
{
    extension(IServiceCollection services)
    {
        public SharpLinkServerBuilder AddSharpLinkServer()
        {
            var builder = SharpLinkServerBuilder.Create();
            services.TryAddSingleton(builder);
            services.AddHostedService<SharpLinkServerHostedService>();
            return builder;
        }

        public SharpLinkServerBuilder AddSharpLinkServer(Action<SharpLinkServerBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var builder = SharpLinkServerBuilder.Create();
            configure(builder);
            services.TryAddSingleton(builder);
            services.AddHostedService<SharpLinkServerHostedService>();
            return builder;
        }

        public SharpClientBuilder AddSharpLinkClient()
        {
            var builder = SharpClientBuilder.Create();
            services.TryAddSingleton(builder);
            services.TryAddSingleton<SharpLinkClientAccessor>();
            services.TryAddSingleton<ISharpLinkClientAccessor>(sp => sp.GetRequiredService<SharpLinkClientAccessor>());
            services.AddHostedService<SharpLinkClientHostedService>();
            return builder;
        }

        public SharpClientBuilder AddSharpLinkClient(Action<SharpClientBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(configure);

            var builder = SharpClientBuilder.Create();
            configure(builder);
            services.TryAddSingleton(builder);
            services.TryAddSingleton<SharpLinkClientAccessor>();
            services.TryAddSingleton<ISharpLinkClientAccessor>(sp => sp.GetRequiredService<SharpLinkClientAccessor>());
            services.AddHostedService<SharpLinkClientHostedService>();
            return builder;
        }
    }
}
