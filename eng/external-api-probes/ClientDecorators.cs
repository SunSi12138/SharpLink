// Compiled as an external, non-IVT consumer. These decorators forward issued handles;
// they do not manufacture local selectors, proxy/channel pairings, or lifecycle state.
public sealed class ExternalClientDecorator(ISharpLinkClient inner) : ISharpLinkClient
{
    public IRpcRuntimeContext RuntimeContext => inner.RuntimeContext;
    public SharpLinkConnectionState State => inner.State;
    public SharpLinkClientLifecycleState LifecycleState => inner.LifecycleState;
    public SharpLinkReadinessState Readiness => inner.Readiness;
    public SharpLinkClusterState ClusterState => inner.ClusterState;
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
    public ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default) => inner.WaitForReadyAsync(cancellationToken);
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => inner.WaitForShutdownAsync(cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    public SharpLinkClientReadinessSnapshot GetReadinessSnapshot() => inner.GetReadinessSnapshot();
    public ValueTask<SharpLinkClientReadinessSnapshot> WaitForReadinessAsync(int minimumReadyEndpoints,
        CancellationToken cancellationToken = default) => inner.WaitForReadinessAsync(minimumReadyEndpoints, cancellationToken);
    public ValueTask WaitForRemoteContractAsync(long contractId, RpcHash128 rpcAssemblyHash,
        CancellationToken cancellationToken = default) => inner.WaitForRemoteContractAsync(contractId, rpcAssemblyHash, cancellationToken);
    public ValueTask WaitForRemoteContractAsync(long contractId, RpcHash128 rpcAssemblyHash, RpcHash128 previousRpcAssemblyHash,
        CancellationToken cancellationToken = default) => inner.WaitForRemoteContractAsync(contractId, rpcAssemblyHash, previousRpcAssemblyHash, cancellationToken);
    public void ReplaceInterceptors(IEnumerable<ISharpLinkClientInterceptor> interceptors) => inner.ReplaceInterceptors(interceptors);
    public SharpLinkReconnectPolicy GetReconnectPolicy() => inner.GetReconnectPolicy();
    public void UpdateReconnectPolicy(SharpLinkReconnectPolicy policy) => inner.UpdateReconnectPolicy(policy);
    public SharpLinkHeartbeatConfigurationSnapshot GetHeartbeatConfigurationSnapshot() => inner.GetHeartbeatConfigurationSnapshot();
    public void UpdateHeartbeat(TimeSpan interval, TimeSpan timeout) => inner.UpdateHeartbeat(interval, timeout);
    public void UpdateHeartbeatInterval(TimeSpan interval) => inner.UpdateHeartbeatInterval(interval);
    public void UpdateHeartbeatTimeout(TimeSpan timeout) => inner.UpdateHeartbeatTimeout(timeout);
    public SharpLinkRequestTimeoutPolicySnapshot GetRequestTimeoutPolicySnapshot() => inner.GetRequestTimeoutPolicySnapshot();
    public void UpdateRequestTimeout(TimeSpan timeout) => inner.UpdateRequestTimeout(timeout);
    public void DisableRequestTimeout() => inner.DisableRequestTimeout();
    public SharpLinkRetryPolicySnapshot GetRetryPolicySnapshot() => inner.GetRetryPolicySnapshot();
    public void UpdateRetryPolicy(ISharpLinkRetryOptions options) => inner.UpdateRetryPolicy(options);
    public void UpdateRetryPolicy(ISharpLinkRetryPolicy policy) => inner.UpdateRetryPolicy(policy);
    public void UpdateRetryPolicy(ISharpLinkRetryPolicy policy, ISharpLinkRetryOptions limits) => inner.UpdateRetryPolicy(policy, limits);
    public void DisableRetry() => inner.DisableRetry();
    public SharpLinkEndpointAdmissionPolicySnapshot GetEndpointAdmissionPolicySnapshot() => inner.GetEndpointAdmissionPolicySnapshot();
    public void UpdateEndpointAdmissionPolicy(ISharpLinkEndpointAdmissionPolicy policy) => inner.UpdateEndpointAdmissionPolicy(policy);
    public void DisableEndpointAdmissionPolicy() => inner.DisableEndpointAdmissionPolicy();
    public SharpLinkCircuitBreakerPolicySnapshot GetCircuitBreakerPolicySnapshot() => inner.GetCircuitBreakerPolicySnapshot();
    public void UpdateCircuitBreaker(ISharpLinkCircuitBreakerOptions options) => inner.UpdateCircuitBreaker(options);
    public void DisableCircuitBreaker() => inner.DisableCircuitBreaker();
    public void UpdateRequestCompressionPolicy(SharpLinkCompressionSendPolicy policy) => inner.UpdateRequestCompressionPolicy(policy);
    public ValueTask SetResponseCompressionPreferenceAsync(bool allowResponseCompression,
        CancellationToken cancellationToken = default) => inner.SetResponseCompressionPreferenceAsync(allowResponseCompression, cancellationToken);
    public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        => inner.CheckHealthAsync(cancellationToken);
    public SharpLinkAssemblyRegistrationResult RegisterAssembly(System.Reflection.Assembly assembly) => inner.RegisterAssembly(assembly);
    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(System.Reflection.Assembly assembly,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
        => inner.UnregisterAssemblyAsync(assembly, gracefulTimeout, cancellationToken);
    public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(System.Reflection.Assembly oldAssembly,
        System.Reflection.Assembly newAssembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
        => inner.ReplaceAssemblyAsync(oldAssembly, newAssembly, gracefulTimeout, cancellationToken);
    public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
        => inner.CreateProxy<TContract>(options);
    public RpcLocalContract GetLocalContract<TContract>() where TContract : IService => inner.GetLocalContract<TContract>();
    public RpcLocalContract GetLocalContract(System.Reflection.Assembly registeredAssembly, long contractId)
        => inner.GetLocalContract(registeredAssembly, contractId);
    public IRpcContractChannel CreateContractChannel(RpcLocalContract local, RpcProxyOptions? options = null)
        => inner.CreateContractChannel(local, options);
}

public sealed class ExternalMultiClusterDecorator(ISharpLinkMultiClusterClient inner) : ISharpLinkMultiClusterClient
{
    public SharpLinkMultiClusterState State => inner.State;
    public SharpLinkClientLifecycleState LifecycleState => inner.LifecycleState;
    public SharpLinkReadinessState Readiness => inner.Readiness;
    public ValueTask StartAsync(CancellationToken cancellationToken = default) => inner.StartAsync(cancellationToken);
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default) => inner.ConnectAsync(cancellationToken);
    public ValueTask WaitForReadyAsync(CancellationToken cancellationToken = default) => inner.WaitForReadyAsync(cancellationToken);
    public ValueTask WaitForReadyAsync(SharpLinkClusterKey cluster, CancellationToken cancellationToken = default)
        => inner.WaitForReadyAsync(cluster, cancellationToken);
    public Task WaitForShutdownAsync(CancellationToken cancellationToken = default) => inner.WaitForShutdownAsync(cancellationToken);
    public ValueTask StopAsync(CancellationToken cancellationToken = default) => inner.StopAsync(cancellationToken);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
    public SharpLinkConnectionState GetClusterState(SharpLinkClusterKey cluster) => inner.GetClusterState(cluster);
    public SharpLinkClusterState GetClusterRuntimeState(SharpLinkClusterKey cluster) => inner.GetClusterRuntimeState(cluster);
    public SharpLinkReadinessState GetClusterReadiness(SharpLinkClusterKey cluster) => inner.GetClusterReadiness(cluster);
    public bool TryGetClusterStatus(SharpLinkClusterKey cluster, out SharpLinkClusterStatusSnapshot status)
        => inner.TryGetClusterStatus(cluster, out status);
    public ValueTask WaitForRemoteContractAsync(SharpLinkClusterKey cluster, long contractId,
        RpcHash128 rpcAssemblyHash, CancellationToken cancellationToken = default)
        => inner.WaitForRemoteContractAsync(cluster, contractId, rpcAssemblyHash, cancellationToken);
    public ValueTask WaitForRemoteContractAsync(SharpLinkClusterKey cluster, long contractId,
        RpcHash128 rpcAssemblyHash, RpcHash128 previousRpcAssemblyHash, CancellationToken cancellationToken = default)
        => inner.WaitForRemoteContractAsync(cluster, contractId, rpcAssemblyHash, previousRpcAssemblyHash, cancellationToken);
    public ValueTask<SharpLinkHealthCheckResult> CheckHealthAsync(SharpLinkClusterKey cluster,
        CancellationToken cancellationToken = default) => inner.CheckHealthAsync(cluster, cancellationToken);
    public SharpLinkAssemblyRegistrationResult RegisterAssembly(SharpLinkClusterKey cluster, System.Reflection.Assembly assembly)
        => inner.RegisterAssembly(cluster, assembly);
    public ValueTask<SharpLinkAssemblyUnregisterResult> UnregisterAssemblyAsync(SharpLinkClusterKey cluster,
        System.Reflection.Assembly assembly, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
        => inner.UnregisterAssemblyAsync(cluster, assembly, gracefulTimeout, cancellationToken);
    public ValueTask<SharpLinkAssemblyReplacementResult> ReplaceAssemblyAsync(SharpLinkClusterKey cluster,
        System.Reflection.Assembly oldAssembly, System.Reflection.Assembly newAssembly,
        TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
        => inner.ReplaceAssemblyAsync(cluster, oldAssembly, newAssembly, gracefulTimeout, cancellationToken);
    public RpcProxy<TContract> CreateProxy<TContract>(RpcProxyOptions? options = null) where TContract : IService
        => inner.CreateProxy<TContract>(options);
    public RpcLocalContract GetLocalContract(SharpLinkClusterKey cluster, System.Reflection.Assembly registeredAssembly, long contractId)
        => inner.GetLocalContract(cluster, registeredAssembly, contractId);
    public IRpcContractChannel CreateContractChannel(SharpLinkClusterKey cluster, RpcLocalContract local, RpcProxyOptions? options = null)
        => inner.CreateContractChannel(cluster, local, options);
}
