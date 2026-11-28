namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
    [Test]
    [NotInParallel]
    [Arguments("tcp", true)]
    [Arguments("shared-memory", true)]
    [Arguments("tcp", false)]
    [Arguments("shared-memory", false)]
    public Task NStudySynchronousPreparationDoesNotKeepRetiredRegistration(string transport, bool inlineDecode)
    {
        var decoder = new NStudyHeldServerDecoder();
        var selectorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var selectorRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long selectedContract = 0;
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            async (fixture, trace, ct) =>
            {
                var (server, contracts, oldAssembly, newAssembly, oldType, _, first, secondOrNull, _) = fixture;
                var second = secondOrNull ?? throw new InvalidOperationException("Expected an independent connection.");
                var oldRegistration = ObserveDecoderRegistration(first.ServerConnection, first.A.BindingId);
                var module = GetDynamicModule(server, oldAssembly);
                Volatile.Write(ref selectedContract, first.A.Contract);
                try
                {
                    Task<int> pending;
                    if (inlineDecode)
                    {
                        var payloadType = contracts.GetContractType("SharpLink.DynamicPlugin.DynamicPayload");
                        var payload = Activator.CreateInstance(payloadType)!;
                        payloadType.GetProperty("Label")!.SetValue(payload, new string('x', 4096));
                        pending = InvokeValueTaskAsync<int>(first.Receiver.Service, contracts.ContractType,
                            "UsePayloadAsync", payload, ct).AsTask();
                        _ = await decoder.FirstEntered.Task.WaitAsync(ct);
                        Ensure(server.DecodeStartedWorkCountForDiagnostics == 0,
                            "this ordinary 4 KiB request decodes inline, not in the persistent executor");
                    }
                    else
                    {
                        pending = InvokeValueTaskAsync<int>(first.Receiver.Service, contracts.ContractType,
                            "UnaryAsync", 7, ct).AsTask();
                        await selectorEntered.Task.WaitAsync(ct);
                    }

                    Ensure(module.RemainingCalls == 0 && !pending.IsCompleted,
                        "the held provider or selector has not entered application execution");
                    var retired = await server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(5), ct)
                        .AsTask().WaitAsync(ct).ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
                    EnsureReplacementReleased(retired, "preparation does not block the original registration's release");
                    await CollectPreparedRegistrationAsync(oldRegistration, ct);
                    Ensure(!oldRegistration.IsAlive && !pending.IsCompleted &&
                           await second.Sibling.TouchAsync(6, ct) == 106,
                        "the synchronous preparation still runs without rooting the old registration or affecting a different connection");

                    decoder.Release();
                    selectorRelease.TrySetResult();
                    await EnsureQueuedSelectionUnavailableAsync(pending);
                    Ensure(NStudyCount(oldType, "Created") == 0,
                        "the old request may not activate the retired registration after preparation");
                }
                finally
                {
                    decoder.Release();
                    selectorRelease.TrySetResult();
                }
            },
            configureClient: inlineDecode ? builder =>
                builder.UseRuntime(options => options.Compression.Providers.Add(new TestCompressionProvider()))
                    .UseRequestCompressionPolicy(new SharpLinkCompressionSendPolicy
                    {
                        MinimumPayloadBytes = 256,
                        MinimumSavingsBytes = 8,
                        MinimumSavingsRatio = 0.05
                    }) : null,
            configureServer: builder =>
            {
                if (inlineDecode)
                    builder.UseRuntime(options => options.Compression.Providers.Add(decoder));
                else
                    builder.UseAdmissionControl(options => options.UsePartition(context =>
                    {
                        if (context.ContractId == Volatile.Read(ref selectedContract))
                        {
                            selectorEntered.TrySetResult();
                            selectorRelease.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                        }
                        return "normal-tenant";
                    }, partition =>
                    {
                        partition.MaxPartitions = 8;
                        partition.UseConcurrency(8);
                    }));
            });
    }

    [Test]
    [NotInParallel]
    [Arguments("tcp")]
    [Arguments("shared-memory")]
    public Task NStudyDynamicExecutionOwnsOldModuleThroughExceptionMapping(string transport)
    {
        var mapper = new HeldDynamicExceptionMapper();
        return WithNStudyCoordinationPeersAsync(transport, NStudyCoordinationMode.Manual, twoPeers: true,
            async (fixture, trace, ct) =>
            {
                var (server, contracts, oldAssembly, newAssembly, oldType, _, first, secondOrNull, _) = fixture;
                var second = secondOrNull ?? throw new InvalidOperationException("Expected an independent connection.");
                var module = GetDynamicModule(server, oldAssembly);
                try
                {
                    oldType.GetMethod("FailNextUnary")!.Invoke(null, null);
                    var pending = InvokeValueTaskAsync<int>(
                        first.Receiver.Service, contracts.ContractType, "UnaryAsync", 17, ct).AsTask();
                    await mapper.Entered.Task.WaitAsync(ct);

                    var replacing = server.ReplaceAssemblyAsync(oldAssembly, newAssembly, TimeSpan.FromSeconds(10), ct).AsTask();
                    await WaitForNStudyAsync(() => module.State != SharpLinkDynamicModuleState.Running, ct);
                    Ensure(module.RemainingCalls == 1 && !replacing.IsCompleted && NStudyCount(oldType, "Disposed") == 0,
                        "the original module still owns the blocked application exception mapper");
                    Ensure(await second.Sibling.TouchAsync(5, ct) == 105,
                        "normal traffic on the other physical connection is independent");

                    mapper.Release();
                    try
                    {
                        _ = await pending.WaitAsync(ct);
                        throw new InvalidOperationException("The caller must observe the mapped business error.");
                    }
                    catch (SharpLinkException error) when (error.Code == SharpLinkErrorCode.FailedPrecondition)
                    {
                    }
                    EnsureReplacementReleased(await replacing.WaitAsync(ct), "mapper exit permits original retirement");
                    Ensure(NStudyCount(oldType, "Disposed") == 1 && mapper.Invocations == 1,
                        "the old service retires exactly once and the mapping is not retried");
                }
                finally
                {
                    mapper.Release();
                }
            },
            configureServer: builder => builder.UseExceptionMapper(mapper));
    }

    private sealed class HeldDynamicExceptionMapper : IRpcExceptionMapper
    {
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _invocations;

        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Invocations => Volatile.Read(ref _invocations);
        internal void Release() => _release.TrySetResult();

        public SharpLinkException Map(Exception exception, SharpLinkServerInvocationContext context)
        {
            if (exception is InvalidOperationException { Message: "The dynamic service could not complete this call." })
            {
                Interlocked.Increment(ref _invocations);
                Entered.TrySetResult();
                _release.Task.WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
                return new SharpLinkException(SharpLinkErrorCode.FailedPrecondition, "Business operation unavailable.");
            }

            return exception as SharpLinkException ??
                new SharpLinkException(SharpLinkErrorCode.Internal, "Unexpected service failure.", exception);
        }
    }
}
