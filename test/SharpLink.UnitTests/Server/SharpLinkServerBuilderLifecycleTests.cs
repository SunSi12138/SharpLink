using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;
using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;

namespace SharpLink.UnitTests.Server;

public partial class SharpLinkServerInvocationTests
{
    [Test]
    public async Task BuilderShouldPublishImmutableFiveSecondShutdownCleanupPlan()
    {
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(new IdleListener())
            .Build();

        Ensure(server.ShutdownPlanForDiagnostics.CleanupBudget == TimeSpan.FromSeconds(5),
            "builder must publish the existing five-second cleanup budget as an immutable plan");
        Ensure(ReferenceEquals(server.ShutdownPlanForDiagnostics, ServerShutdownPlan.Default),
            "the default server path must consume the validated shared shutdown plan snapshot");

        await server.StopAsync(TimeSpan.Zero);
    }

    [Test]
    public async Task BuilderShouldForwardTheApplicationOwnedTimeProvider()
    {
        var timeProvider = new ManualTimeProvider();
        var server = (SharpLinkServer)SharpLinkServerBuilder.Create().UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTimeProvider(timeProvider)
            .UseTransport(new IdleListener())
            .Build();
        var runtimeContext = (SharpLinkRuntimeContext)(
            typeof(SharpLinkServer).GetField(
                "_runtimeContext",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(server)!);

        Ensure(ReferenceEquals(runtimeContext.TimeProvider, timeProvider),
            "server builder must preserve the configured provider instance");
        await server.StopAsync(TimeSpan.Zero);
        Ensure(timeProvider.ActiveTimerCount == 0,
            "stopping the server must release its timer without disposing the application-owned provider");
    }
}
