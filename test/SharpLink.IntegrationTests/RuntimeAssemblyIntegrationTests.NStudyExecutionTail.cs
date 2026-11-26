namespace SharpLink.IntegrationTests;

public sealed partial class RuntimeAssemblyIntegrationTests
{
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
