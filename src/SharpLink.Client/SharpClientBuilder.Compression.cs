namespace SharpLink.Client;

public partial class SharpClientBuilder
{
    private SharpLinkCompressionSendPolicy _requestCompressionPolicy = new();
    private Func<CancellationToken, ValueTask>? _beforeReadyPublicationTestHook;

    /// <summary>Configures instance-scoped runtime capability and behavior.</summary>
    public SharpClientBuilder UseRuntime(Action<SharpLinkRuntimeOptions> configure)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(configure);
            _runtimeContextBuilder.Configure(configure);
        });
        return this;
    }

    /// <summary>Configures the initial Client Request compression send policy.</summary>
    public SharpClientBuilder UseRequestCompressionPolicy(SharpLinkCompressionSendPolicy policy)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(policy);
            _ = CompressionSendPolicySnapshot.CreateValidated(policy);
            _requestCompressionPolicy = policy;
        });
        return this;
    }

    internal SharpClientBuilder UseBeforeReadyPublicationTestHook(Func<CancellationToken, ValueTask> hook)
    {
        Configure(() => _beforeReadyPublicationTestHook = hook ?? throw new ArgumentNullException(nameof(hook)));
        return this;
    }
}
