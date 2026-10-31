using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("unary")]
    [Arguments("no-return")]
    [Arguments("one-way")]
    [Arguments("client-stream")]
    [Arguments("server-stream")]
    [Arguments("duplex")]
    [Arguments("one-way-stream")]
    public async Task PreparedFactsDoNotMoveModuleOwnershipAcrossThePolicyWait(string shape)
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load($"prepared-ownership-{shape}");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = await CreatePreparedDynamicProxyAsync(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var module = GetDynamicModule(server, plugin.ServiceAssembly);
        var holder = await HoldQueuedAdmissionAsync(server);
        using var releaseBefore = new ManualResetEventSlim();
        using var releaseAfter = new ManualResetEventSlim();
        var before = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var after = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var acquisitions = 0;
        server.BeforeInvocationModuleAcquireForTesting = registration =>
        {
            if (!ReferenceEquals(registration.Module, module)) return;
            before.TrySetResult();
            if (!releaseBefore.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Release pre-acquire barrier.");
        };
        server.AfterInvocationModuleAcquireForTesting = registration =>
        {
            if (!ReferenceEquals(registration.Module, module)) return;
            Interlocked.Increment(ref acquisitions);
            after.TrySetResult();
            if (!releaseAfter.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Release post-acquire barrier.");
        };
        Task? call = null;
        try
        {
            call = StartQueuedShape(proxy, plugin.ContractType, shape, CancellationToken.None);
            await WaitUntilAsync(() => server.CurrentAdmissionProgramForTests!.Kernel.QueuedCalls == 1);
            Ensure(module.RemainingCalls == 0 && module.RemainingStreams == 0 && server.ActiveCallCountForDiagnostics == 0,
                "policy wait holds neither module invocation ownership nor an earlier request permit");
            holder.Lease!.Dispose();
            await before.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(server.ActiveCallCountForDiagnostics == 1 && module.RemainingCalls == 0,
                "the original request permit is acquired before the original module gate");
            Ensure(plugin.GetStaticInt("Created") == 0 && server.ActiveDecodeCountForDiagnostics == 0,
                "no service activation or decode precedes module admission");
            releaseBefore.Set();
            await after.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams ==
                (shape is "client-stream" or "server-stream" or "duplex" or "one-way-stream" ? 1 : 0),
                "one original lease is classified from complete facts, including OneWay input streams");
            Ensure(plugin.GetStaticInt("Created") == 0, "activation has not acquired a second lease");
            releaseAfter.Set();
            await call.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntilAsync(() => module.RemainingCalls == 0 && server.ActiveCallCountForDiagnostics == 0);
            Ensure(acquisitions == 1 && module.RemainingStreams == 0, "one acquisition and one physical completion");
        }
        finally
        {
            releaseBefore.Set(); releaseAfter.Set(); holder.Lease!.Dispose();
            server.BeforeInvocationModuleAcquireForTesting = null;
            server.AfterInvocationModuleAcquireForTesting = null;
            if (call is not null) await ObserveQueuedTerminalAsync(call);
        }
        // The OneWay-input-stream arm is an explicit remaining type23/producer gate. Do not
        // delete or weaken it to report the unrelated four-family source as all-five acceptance.
    }

    [Test]
    [NotInParallel]
    public async Task RetirementBetweenWeakRecoveryAndTheOriginalGateRejectsWithoutDecodeOrActivation()
    {
        await using var harness = await DynamicHarness.CreateAsync();
        using var plugin = PluginBundle.Load("retirement-before-original-gate");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = await CreatePreparedDynamicProxyAsync(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var module = GetDynamicModule(server, plugin.ServiceAssembly);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        server.BeforeInvocationModuleAcquireForTesting = registration =>
        {
            if (!ReferenceEquals(registration.Module, module)) return;
            recovered.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Release recovered original identity.");
        };
        var call = InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UnaryAsync", 1, CancellationToken.None).AsTask();
        try
        {
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(module.RemainingCalls == 0 && server.ActiveCallCountForDiagnostics == 1, "identity recovery is not a module lease");
            var retired = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
            Ensure(retired.ReferencesReleased, "zero-module-owner retirement may release the selected payload");
            release.Set();
            await EnsureQueuedSelectionUnavailableAsync(call);
            await WaitUntilAsync(() => server.ActiveCallCountForDiagnostics == 0);
            Ensure(plugin.GetStaticInt("Created") == 0 && server.ActiveDecodeCountForDiagnostics == 0,
                "the losing exact gate never decodes, activates or looks up a replacement");
        }
        finally
        {
            release.Set(); server.BeforeInvocationModuleAcquireForTesting = null;
            await ObserveQueuedTerminalAsync(call);
        }
    }

    [Test]
    [NotInParallel]
    public async Task PersistentDecodeRetainsTheSameOriginalModuleOwnerAcrossRetirement()
    {
        var compression = new PreparedOwnershipBlockingCompression();
        await using var harness = await DynamicHarness.CreateAsync(compressionProvider: compression);
        using var plugin = PluginBundle.Load("prepared-persistent-decode-owner");
        plugin.ResetServiceState();
        await RegisterAllAsync(harness, plugin);
        var proxy = await CreatePreparedDynamicProxyAsync(harness.Client, plugin.ContractType);
        var server = (SharpLinkServer)harness.Server;
        var module = GetDynamicModule(server, plugin.ServiceAssembly);
        var payloadType = plugin.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("Value")!.SetValue(payload, 42);
        payloadType.GetProperty("Label")!.SetValue(payload, new string('p', 2 * 1024 * 1024));
        payloadType.GetProperty("Parent")!.SetValue(payload, payload);
        var call = InvokeValueTaskAsync<int>(proxy, plugin.ContractType, "UsePayloadAsync", payload, CancellationToken.None).AsTask();
        try
        {
            await compression.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(module.RemainingCalls == 1 && server.ActiveCallCountForDiagnostics == 1 &&
                server.ActiveDecodeCountForDiagnostics == 1 && server.DecodeStartedWorkCountForDiagnostics == 1,
                "the running persistent worker is protected by the original post-permit module owner");
            Ensure(plugin.GetStaticInt("Created") == 0, "provider entry is before service activation");
            var retirement = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
            Ensure(!retirement.ReferencesReleased && module.RemainingCalls == 1,
                "logical retirement timeout cannot dispose a noncooperative physical decode owner");
            compression.Release.Set();
            await ObserveQueuedTerminalAsync(call);
            await WaitUntilAsync(() => module.RemainingCalls == 0 && server.ActiveCallCountForDiagnostics == 0);
            Ensure(server.ActiveDecodeCountForDiagnostics == 0 && server.DecodedBytesInFlightForDiagnostics == 0 &&
                server.RetainedCompressedBytesForDiagnostics == 0, "actual worker completion releases resources once");
        }
        finally { compression.Release.Set(); await ObserveQueuedTerminalAsync(call); }
    }

    [Test]
    [NotInParallel]
    public async Task GeneratedDynamicCancellationIsLogicalBeforeOrderedPhysicalScopeCompletion()
    {
        using var plugin = PluginBundle.Load("generated-dynamic-physical-tail");
        const string serviceName = "SharpLink.DynamicPlugin.PhysicalTailOwnershipService";
        const string scopeName = "SharpLink.DynamicPlugin.PhysicalTailScopeProbe";
        plugin.InvokeServiceStatic(serviceName, "Reset");
        var scopeType = plugin.ServiceAssembly.GetType(scopeName, throwOnError: true)!;
        await using var harness = await DynamicHarness.CreateAsync(
            configureServices: services => services.AddScoped(scopeType));
        await RegisterAllAsync(harness, plugin);
        var contract = plugin.GetContractType("SharpLink.DynamicPlugin.IPhysicalTailOwnershipService");
        var proxy = await CreatePreparedDynamicProxyAsync(harness.Client, contract);
        var server = (SharpLinkServer)harness.Server;
        var module = GetDynamicModule(server, plugin.ServiceAssembly);
        var capturedState = new TaskCompletionSource<ServerCallCancellationLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        server.BeforeGeneratedPhysicalInvocationForTesting = lease => capturedState.TrySetResult(lease);
        using var cancellation = new CancellationTokenSource();
        var call = CollectAsync(InvokeStream(proxy, contract, "BlockPhysicalTailAsync", cancellation.Token));
        try
        {
            await plugin.GetServiceStaticTask(serviceName, "MoveEntered").WaitAsync(TimeSpan.FromSeconds(5));
            var originalState = await capturedState.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var originalToken = originalState.State.InvocationToken;
            void AssertOriginalCallState()
            {
                Ensure(originalState.State.CaptureLease(originalState.RequestId).Generation == originalState.Generation &&
                    originalState.State.RequestId == originalState.RequestId && originalState.State.InvocationToken == originalToken &&
                    !originalToken.WaitHandle.SafeWaitHandle.IsClosed,
                    "the original call-state generation/InvocationToken remains live without a test-acquired pin");
            }
            Ensure(module.RemainingCalls == 1 && module.RemainingStreams == 1, "generated stream owns one original module lease");
            cancellation.Cancel();
            try
            {
                await call.WaitAsync(TimeSpan.FromSeconds(5));
                throw new InvalidOperationException("Cancelled generated stream unexpectedly succeeded.");
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            // Cancellation is observable while the producer is still blocked, not after join.
            AssertOriginalCallState();
            Ensure(!plugin.GetServiceStaticTask(serviceName, "EnumeratorDisposeEntered").IsCompleted,
                "logical cancellation does not dispose an enumerator concurrently with its MoveNext");
            var retired = await server.UnregisterAssemblyAsync(plugin.ServiceAssembly, TimeSpan.Zero);
            Ensure(!retired.ReferencesReleased && module.RemainingCalls == 1 &&
                plugin.GetServiceStaticInt(serviceName, "ServiceDisposed") == 0 &&
                plugin.GetServiceStaticInt(scopeName, "Disposed") == 0,
                "logical retirement cannot release module/service/scope while the producer runs");

            plugin.InvokeServiceStatic(serviceName, "ReleaseMove");
            await plugin.GetServiceStaticTask(serviceName, "EnumeratorDisposeEntered").WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(plugin.GetServiceStaticInt(serviceName, "TokenChecks") == 1 && module.RemainingCalls == 1 &&
                !plugin.GetServiceStaticTask(serviceName, "ServiceDisposeEntered").IsCompleted,
                "MoveNext observed live scope/linked CTS and ordered enumerator disposal still holds the module");
            AssertOriginalCallState();
            plugin.InvokeServiceStatic(serviceName, "ReleaseEnumeratorDispose");
            await plugin.GetServiceStaticTask(serviceName, "ServiceDisposeEntered").WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(plugin.GetServiceStaticInt(serviceName, "TokenChecks") == 2 &&
                plugin.GetServiceStaticInt(serviceName, "EnumeratorDisposed") == 1 && module.RemainingCalls == 1 &&
                !plugin.GetServiceStaticTask(scopeName, "DisposeEntered").IsCompleted,
                "enumerator Dispose completed once before service disposal, without an early module release");
            AssertOriginalCallState();
            plugin.InvokeServiceStatic(serviceName, "ReleaseServiceDispose");
            await plugin.GetServiceStaticTask(scopeName, "DisposeEntered").WaitAsync(TimeSpan.FromSeconds(5));
            Ensure(plugin.GetServiceStaticInt(serviceName, "ServiceDisposed") == 1 && module.RemainingCalls == 1,
                "service disposal completed but its actual DI scope still owns the physical module fence");
            AssertOriginalCallState();
            plugin.InvokeServiceStatic(scopeName, "ReleaseDispose");
            await WaitUntilAsync(() => module.RemainingCalls == 0 && server.ActiveCallCountForDiagnostics == 0);
            Ensure(!originalState.TryAcquire(), "the original state may retire only after all physical cleanup has finished");
            Ensure(module.RemainingStreams == 0 && plugin.GetServiceStaticInt(scopeName, "Disposed") == 1,
                "actual scope completion releases the sole module/request owner exactly once");
        }
        finally
        {
            server.BeforeGeneratedPhysicalInvocationForTesting = null;
            plugin.InvokeServiceStatic(serviceName, "ReleaseMove");
            plugin.InvokeServiceStatic(serviceName, "ReleaseEnumeratorDispose");
            plugin.InvokeServiceStatic(serviceName, "ReleaseServiceDispose");
            plugin.InvokeServiceStatic(scopeName, "ReleaseDispose");
            await ObserveQueuedTerminalAsync(call);
        }
    }

    private static async Task<object> CreatePreparedDynamicProxyAsync(ISharpLinkClient client, Type contract)
    {
        var handle = typeof(ISharpLinkClient).GetMethod(nameof(ISharpLinkClient.CreateProxy))!
            .MakeGenericMethod(contract).Invoke(client, new object?[] { null })!;
        var prepare = (ValueTask)handle.GetType().GetMethod("PrepareAsync")!
            .Invoke(handle, new object?[] { TimeSpan.FromSeconds(5), CancellationToken.None })!;
        await prepare;
        return handle.GetType().GetProperty("Service")!.GetValue(handle)!;
    }

    private sealed class PreparedOwnershipBlockingCompression : ISharpLinkCompressionProvider
    {
        private readonly TestCompressionProvider _inner = new();
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new();
        public string WireProfile => _inner.WireProfile;
        public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default) => _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
        public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Release physical decode barrier.");
            _inner.Decompress(input, output, maxOutputBytes, cancellationToken);
        }
    }
}
