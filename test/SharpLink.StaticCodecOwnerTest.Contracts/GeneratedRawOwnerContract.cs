using SharpLink.Sdk;

namespace SharpLink.StaticCodecOwnerTest.Contracts;

public struct GeneratedOwnerRawPayload
{
    public long Stamp;
    public int Count;
}

[RpcContract]
public interface IGeneratedRawOwnerContract : IService
{
    ValueTask<GeneratedOwnerRawPayload> EchoAsync(GeneratedOwnerRawPayload value, CancellationToken cancellationToken);
}
