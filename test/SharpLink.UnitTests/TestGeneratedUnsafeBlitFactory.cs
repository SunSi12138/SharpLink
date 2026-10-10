using SharpLink.Abstractions;

namespace SharpLink.UnitTests;

internal sealed class TestGeneratedUnsafeBlitFactory<T> : ITestGeneratedCodecFactory, IRpcGeneratedUnsafeBlitCodecFactory
{
    public Type TargetType => typeof(T);
    public SharpLinkGeneratedUnsafeBlitRequirement Requirement => new(8, false);
    public string? AdapterId => null;
    public IRpcCodecAdapter? Adapter => null;

    public IRpcCodec Create(IRpcCodecProvider provider, IRpcCodecAdapterScope? adapterScope)
        => ((IRpcGeneratedUnsafeBlitCodecProvider)provider).GetGeneratedUnsafeBlitCodec<T>(this, Requirement);

    public bool IsCompatibleCodec(IRpcCodec codec) => codec is IRpcCodec<T>;
}
