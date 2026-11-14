using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.UnitTests;

public sealed class RuntimeConfigurationUnsupportedImplementationTests
{
    [Test]
    public void CustomClientShouldReceiveStructuredUnsupportedResult()
    {
        ISharpLinkClient client = new CustomClient();

        var result = client.TryUpdateRequestTimeout(TimeSpan.FromSeconds(1));
        var flushResult = client.TryUpdateRpcSessionFlushPolicy(
            4096,
            TimeSpan.FromMilliseconds(5));
        var invalidFlush = CaptureException(() =>
            client.TryUpdateRpcSessionFlushPolicy(0, TimeSpan.FromMilliseconds(1)));

        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom client should receive explicit non-throwing unsupported result");
        Ensure(!flushResult.Succeeded &&
               flushResult.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom client flush update should receive explicit non-throwing unsupported result");
        Ensure(invalidFlush is ArgumentOutOfRangeException,
            "invalid custom-client flush input must remain a parameter exception");
    }

    [Test]
    public void CustomServerShouldReceiveStructuredUnsupportedResult()
    {
        ISharpLinkServer server = new CustomServer();

        var result = server.TryDisableAdmissionControl();
        var flushResult = server.TryUpdateRpcSessionFlushPolicy(
            4096,
            TimeSpan.FromMilliseconds(5));
        var invalidFlush = CaptureException(() =>
            server.TryUpdateRpcSessionFlushPolicy(0, TimeSpan.FromMilliseconds(1)));

        Ensure(!result.Succeeded &&
               result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom server should receive explicit non-throwing unsupported result");
        Ensure(!flushResult.Succeeded &&
               flushResult.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.UnsupportedByImplementation,
            "custom server flush update should receive explicit non-throwing unsupported result");
        Ensure(invalidFlush is ArgumentOutOfRangeException,
            "invalid custom-server flush input must remain a parameter exception");
    }

    [Test]
    public async Task CustomServerReceiptApisMustRejectWithoutInvokingLegacyRegistry()
    {
        var custom = new CustomServer();
        ISharpLinkServer server = custom;
        var assembly = typeof(RuntimeConfigurationUnsupportedImplementationTests).Assembly;
        Ensure(CaptureException(() => server.RegisterAssemblyWithReceipts(assembly)) is NotSupportedException,
            "unsupported receipt registration must not call the legacy register entry");
        Ensure(CaptureException(() => server.RegisterAssemblyWithReceipts(null!)) is ArgumentNullException,
            "invalid receipt registration input wins before unsupported implementation");
        try
        {
            await server.ReplaceAssemblyWithReceiptsAsync(assembly, assembly, TimeSpan.Zero);
            throw new Exception("expected unsupported receipt replacement");
        }
        catch (NotSupportedException) { }
        Ensure(CaptureException(() => server.ReplaceAssemblyWithReceiptsAsync(assembly, assembly,
                TimeSpan.FromTicks(-1))) is ArgumentOutOfRangeException,
            "invalid receipt replacement budget wins before unsupported implementation");
        Ensure(custom.RegistryCalls == 0, "unsupported receipt APIs must never fall back to old registry mutations");
    }

    private static Exception CaptureException(Action action)
    {
        try
        {
            action();
            throw new Exception("expected exception");
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class CustomClient : ISharpLinkClient
    {
        public SharpLinkConnectionState State => SharpLinkConnectionState.Created;

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkHealthCheckResult>(new NotSupportedException());

        public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
            => throw new NotSupportedException();
        public IRpcRuntimeContext RuntimeContext => throw new NotSupportedException();
        public RpcLocalContract GetLocalContract<T>() where T : IService => throw new NotSupportedException();
        public RpcLocalContract GetLocalContract(System.Reflection.Assembly assembly, long contractId)
            => throw new NotSupportedException();
        public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
            => throw new NotSupportedException();

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
            => throw new NotSupportedException();

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CustomServer : ISharpLinkServer
    {
        internal int RegistryCalls { get; private set; }

        public RpcRegistrationReceipt GetRegistrationReceipt(System.Reflection.Assembly assembly, long contractId)
            => throw new NotSupportedException();

        public SharpLinkServerLifecycleState LifecycleState => SharpLinkServerLifecycleState.Created;

        public SharpLinkHealthStatus HealthStatus => SharpLinkHealthStatus.Unhealthy;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public Task WaitForShutdownAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public ValueTask StopAsync(
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;

        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly)
        {
            RegistryCalls++;
            throw new NotSupportedException();
        }

        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(
            Assembly assembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<SharpLinkAssemblyUnregisterResult>(new NotSupportedException());

        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(
            Assembly oldAssembly,
            Assembly newAssembly,
            TimeSpan gracefulTimeout,
            CancellationToken cancellationToken = default)
        {
            RegistryCalls++;
            return ValueTask.FromException<SharpLinkAssemblyReplacementResult>(new NotSupportedException());
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
