namespace SharpLink.Runtime;
/// <summary>
/// 用于向Host中的其他服务暴露，来重复建立多个匿名管道连接
/// </summary>
public interface IAnonymousPipeAllocator
{
    /// <summary>Offers one new anonymous-pipe connection to the bounded server accept queue.</summary>
    /// <example><code>var offer = await allocator.AllocateAsync(cancellationToken);</code></example>
    ValueTask<AnonymousPipeOffer> AllocateAsync(CancellationToken cancellationToken = default);
}
