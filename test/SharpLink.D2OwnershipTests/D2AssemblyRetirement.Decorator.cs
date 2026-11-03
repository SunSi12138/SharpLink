using System.Net;
using System.Reflection;
using SharpLink.Client;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Client;

public sealed partial class D2AssemblyRetirementTests
{
    // Narrow injected child capability: real registry supplies every selected
    // task/certificate. The only fault injection is an independent synchronous
    // decorator stage AFTER the original real child call returned its receipt.
    private sealed class D2ThrowingDecorator(Fixture fixture, TimeProvider provider) :
        ISharpLinkClient, ISharpLinkPreparedAssemblyClient, IDynamicAssemblyRegistrationInspector, ISharpLinkClientTimeProvider
    {
        private SharpLinkClient? _inner;
        internal SharpLinkClient Inner => _inner ?? throw new InvalidOperationException("Actual child not initialized.");
        internal Exception? ThrowAfterSelection;
        internal Task<SharpLinkAssemblyUnregisterResult>? OriginalReturned;
        internal readonly TaskCompletionSource Selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeProvider TimeProvider => provider;
        private ISharpLinkPreparedAssemblyClient Prepared => Inner;
        private void EnsureChild(PreparedGeneratedManifest manifest)
        {
            if (_inner is not null)
            {
                Ensure(ReferenceEquals(((SharpLinkRuntimeContext)_inner.RuntimeContext).Domain.Authority,
                    manifest.Preparation.Authority), "actual decorated child cannot cross authorities");
                return;
            }
            var builder = SharpClientBuilder.Create().DisableRequestTimeout().UseTimeProvider(provider)
                .UseTcp(IPAddress.Loopback.ToString(), 1);
            var domain = manifest.Preparation.Authority.CreateDomain(manifest.Preparation.Domain);
            var plan = builder.CompileForMultiCluster([], domain);
            _inner = (SharpLinkClient)builder.MaterializeCompiledPlan(plan);
        }
        public SharpLinkAssemblyRegistrationResult RegisterPreparedAssembly(PreparedGeneratedManifest manifest,
            Func<Func<SharpLinkAssemblyRegistrationResult>, SharpLinkAssemblyRegistrationResult>? publication = null)
        { EnsureChild(manifest); return Prepared.RegisterPreparedAssembly(manifest, publication); }
        public ClientRetirementBudget CaptureRetirementBudget(TimeSpan gracefulTimeout) => Prepared.CaptureRetirementBudget(gracefulTimeout);
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(Assembly assembly, ClientRetirementBudget budget,
            ChildAssemblySelectionMailbox? mailbox = null)
        {
            var original = fixture.Own(Prepared.UnregisterAssemblyAsync(assembly, budget, mailbox).AsTask());
            OriginalReturned = original;
            if (ThrowAfterSelection is { } failure)
            {
                Ensure(mailbox?.Control is not null && mailbox.Physical is not null && mailbox.Observation is not null,
                    "decorator fault follows actual selected original child roots");
                mailbox.RecordInvocationFailure(failure);
                Selected.TrySetResult();
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            }
            return new(original);
        }
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplacePreparedAssemblyAsync(Assembly oldAssembly,
            PreparedGeneratedManifest manifest, ClientRetirementBudget budget, ChildAssemblySelectionMailbox? mailbox = null)
            => Prepared.ReplacePreparedAssemblyAsync(oldAssembly, manifest, budget, mailbox);
        public SharpLinkConnectionState State => Inner.State;
        public SharpLinkClientLifecycleState LifecycleState => Inner.LifecycleState;
        public SharpLinkReadinessState Readiness => Inner.Readiness;
        public SharpLinkClusterState ClusterState => Inner.ClusterState;
        public IRpcRuntimeContext RuntimeContext => Inner.RuntimeContext;
        public ValueTask StartAsync(CancellationToken token = default) => Inner.StartAsync(token);
        public ValueTask ConnectAsync(CancellationToken token = default) => Inner.ConnectAsync(token);
        public ValueTask WaitForReadyAsync(CancellationToken token = default) => Inner.WaitForReadyAsync(token);
        public Task WaitForShutdownAsync(CancellationToken token = default) => Inner.WaitForShutdownAsync(token);
        public ValueTask StopAsync(CancellationToken token = default) => Inner.StopAsync(token);
        public ValueTask DisposeAsync() => Inner.DisposeAsync();
        public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken token = default) => Inner.CheckHealthAsync(token);
        public RpcProxy<T> CreateProxy<T>(RpcProxyOptions? options = null) where T : IService => Inner.CreateProxy<T>(options);
        public RpcLocalContract GetLocalContract<T>() where T : IService => Inner.GetLocalContract<T>();
        public RpcLocalContract GetLocalContract(Assembly assembly, long id) => Inner.GetLocalContract(assembly, id);
        public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
            => Inner.CreateContractChannel(local, options);
        public SharpLinkAssemblyRegistrationResult RegisterAssembly(Assembly assembly) => Inner.RegisterAssembly(assembly);
        public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(Assembly assembly, TimeSpan timeout,
            CancellationToken token = default) => Inner.UnregisterAssemblyAsync(assembly, timeout, token);
        public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(Assembly oldAssembly, Assembly newAssembly,
            TimeSpan timeout, CancellationToken token = default) => Inner.ReplaceAssemblyAsync(oldAssembly, newAssembly, timeout, token);
        public bool IsDynamicAssemblyRegistered(Assembly assembly) => Inner.IsDynamicAssemblyRegistered(assembly);
    }
}
