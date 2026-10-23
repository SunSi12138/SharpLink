using Microsoft.Extensions.DependencyInjection;
using SharpLink.Server;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Server;

public partial class ServiceRegistrationTests
{
    [Test]
    [Arguments(SharpLinkServiceLifetime.Singleton, false)]
    [Arguments(SharpLinkServiceLifetime.Connection, false)]
    [Arguments(SharpLinkServiceLifetime.Call, false)]
    [Arguments(SharpLinkServiceLifetime.Singleton, true)]
    [Arguments(SharpLinkServiceLifetime.Connection, true)]
    [Arguments(SharpLinkServiceLifetime.Call, true)]
    public async Task DisposedRegistrationWithNoRetiredModulePreservesObjectDisposed(
        SharpLinkServiceLifetime lifetime, bool dynamic)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecs = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(typeof(ServiceRegistrationTests).Assembly, manifest, codecs);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var registration = CreateClassificationRegistration(provider, lifetime, dynamic ? module : null);
        await registration.DisposeAsync();
        var exception = await CaptureAsync(() => AcquireClassificationRegistration(registration, dynamic));
        Ensure(exception is ObjectDisposedException,
            "static disposal and manual disposal of a running module registration retain ObjectDisposedException");
        var expected = new ObjectDisposedException(typeof(ServiceRegistration).FullName);
        Ensure(((ObjectDisposedException)exception!).ObjectName == expected.ObjectName &&
            exception.Message == expected.Message,
            "disposed misuse must preserve the baseline object name and default message");
        Ensure(module.RemainingCalls == 0, "disposed misuse must not acquire a module lease");
    }

    [Test]
    [Arguments(SharpLinkServiceLifetime.Singleton)]
    [Arguments(SharpLinkServiceLifetime.Connection)]
    [Arguments(SharpLinkServiceLifetime.Call)]
    public async Task RetirementAfterSelectionBeforeDisposalRejectsAtExistingModuleGate(
        SharpLinkServiceLifetime lifetime)
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecs = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(typeof(ServiceRegistrationTests).Assembly, manifest, codecs);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var registration = CreateClassificationRegistration(provider, lifetime, module);
        Ensure(registration.AcceptsCalls, "the caller selected a running registration");
        module.TryBeginDraining();
        var exception = await CaptureAsync(() => AcquireClassificationRegistration(registration, dynamic: true));
        Ensure(exception is SharpLinkException { Code: SharpLinkErrorCode.Unavailable },
            "the existing lease gate must reject retirement even before registration disposal");
        Ensure(module.RemainingCalls == 0, "retirement rejection must not acquire a module lease");
        await registration.DisposeAsync();
    }

    [Test]
    public async Task QueuedAttemptsShareTheRegistrationWeakIdentityWithoutAllocating()
    {
        using var context = new SharpLinkRuntimeContextBuilder().Build(includeGeneratedAssemblyCatalog: false);
        var manifest = new EmptyManifest(typeof(ServiceRegistrationTests).Assembly);
        using var codecs = context.PrepareGeneratedManifest(manifest);
        var module = new SharpLinkDynamicModule(typeof(ServiceRegistrationTests).Assembly, manifest, codecs);
        using var provider = new ServiceCollection().BuildServiceProvider();
        var registration = CreateClassificationRegistration(provider, SharpLinkServiceLifetime.Singleton, module);
        var first = new SharpLinkServer.QueuedServiceSelection(registration);
        Ensure(first.TryGetService(out var selected) && ReferenceEquals(selected, registration),
            "the cold weak identity resolves the exact selected registration");
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var index = 0; index < 1_000; index++)
        {
            var selection = new SharpLinkServer.QueuedServiceSelection(registration);
            if (!selection.TryGetService(out selected) || !ReferenceEquals(selected, registration))
                throw new InvalidOperationException("queued selection changed registration");
        }
        Ensure(GC.GetAllocatedBytesForCurrentThread() == before,
            "subsequent queued selections borrow the existing weak object/handle without allocation");
        await registration.DisposeAsync();
    }

    private static ServiceRegistration CreateClassificationRegistration(
        ServiceProvider provider, SharpLinkServiceLifetime lifetime, SharpLinkDynamicModule? module)
        => lifetime switch
        {
            SharpLinkServiceLifetime.Singleton => ServiceRegistration.CreateSingleton(
                typeof(object), new StubMarker(), new object(), ownsService: false, module),
            SharpLinkServiceLifetime.Connection => ServiceRegistration.CreateConnection(
                typeof(object), new StubMarker(), provider.GetRequiredService<IServiceScopeFactory>(),
                static _ => new object(), disposeService: false, module),
            _ => ServiceRegistration.CreatePerCall(
                typeof(object), new StubMarker(), provider.GetRequiredService<IServiceScopeFactory>(),
                static _ => new object(), disposeService: false, module)
        };

    private static ValueTask AcquireClassificationRegistration(ServiceRegistration registration, bool dynamic)
    {
        if (registration.Lifetime == SharpLinkServiceLifetime.Singleton)
        {
            if (dynamic)
            {
                registration.TryAcquireDynamicSingleton(isStream: false, out _, out var moduleLease);
                moduleLease.Dispose();
            }
            else
                registration.TryGetStaticSingleton(out _);
            return ValueTask.CompletedTask;
        }
        return AcquireAndReleaseAsync(registration);
    }

    private static async ValueTask AcquireAndReleaseAsync(ServiceRegistration registration)
    {
        var lease = await registration.AcquireAsync(null!, isStream: false);
        await lease.DisposeAsync();
    }
}
