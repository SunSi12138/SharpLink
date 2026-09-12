namespace SharpLink.Server;

public partial class SharpLinkServerBuilder
{
    private SharpLinkCompressionSendPolicy _responseCompressionPolicy = new();

    /// <summary>Configures the initial Server Response compression send policy.</summary>
    public SharpLinkServerBuilder UseResponseCompressionPolicy(SharpLinkCompressionSendPolicy policy)
    {
        Configure(() =>
        {
            ArgumentNullException.ThrowIfNull(policy);
            _ = CompressionSendPolicySnapshot.CreateValidated(policy);
            _responseCompressionPolicy = policy;
        });
        return this;
    }
}
