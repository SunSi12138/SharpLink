using System.Reflection;
using SharpLink.Abstractions;
[assembly: SharpLinkGeneratedAssemblyManifest(typeof(PreviousAbi.Manifest), 5, 2, "previous-profile-test", "sharplink-experimental-775-stream-admission-v5")]
namespace PreviousAbi;
public sealed class Manifest : ISharpLinkGeneratedAssemblyManifest
{
    public static int Constructions;
    public Manifest() { Interlocked.Increment(ref Constructions); throw new InvalidOperationException("Stale manifest must not be constructed."); }
    public int ApiVersion => 5;
    public int ProtocolVersion => 2;
    public string GeneratorVersion => "previous-profile-test";
    public Assembly OwnerAssembly => typeof(Manifest).Assembly;
    public string CompileTimeDescriptor => "previous-profile-test";
    public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
    public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
    public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
    public IReadOnlyList<string> Dependencies => [];
}
