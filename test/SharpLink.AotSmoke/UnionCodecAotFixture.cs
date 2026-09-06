using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.AotSmoke;

[RpcUnionCase(1, typeof(UnionAotAlpha))]
[RpcUnionCase(2, typeof(UnionAotBeta))]
public interface IUnionAotValue
{
}

[RpcSerializable]
public sealed class UnionAotAlpha : IUnionAotValue
{
    [RpcMember(1)]
    public int Value { get; set; }
}

[RpcSerializable]
public sealed class UnionAotBeta : IUnionAotValue
{
    [RpcMember(1)]
    public string Value { get; set; } = string.Empty;
}

[RpcSerializable]
public sealed class UnionAotEnvelope
{
    [RpcMember(1)]
    public IUnionAotValue? Current { get; set; }

    [RpcMember(2)]
    public List<IUnionAotValue> History { get; set; } = [];
}

[RpcContract]
public interface IUnionAotContract : IService
{
    ValueTask<IUnionAotValue> EchoAsync(
        IUnionAotValue value,
        CancellationToken cancellationToken);

    IAsyncEnumerable<IUnionAotValue> EchoStreamAsync(
        IAsyncEnumerable<IUnionAotValue> values,
        CancellationToken cancellationToken);
}
