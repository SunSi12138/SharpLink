using System.Buffers;
using SharpLink.GenerationControl;
using SharpLink.Runtime;
using SharpLink.Sdk;

[assembly: SharpLinkClusterContractAssembly("generation-control", typeof(ISharpLinkGenerationControl))]

namespace SharpLink.PackageSmoke;

// Linked into the full-trim, partial-trim and NativeAOT package consumers as well.
internal static class GenerationControlSerializationProbe
{
    public static void Run()
    {
        using var runtime = new SharpLinkRuntimeContextBuilder().Build();
        var codec = runtime.Codecs.GetCodec<SharpLinkGenerationInventory>();
        // A restarted authority may publish a lower revision. Serialization must preserve
        // both ordering domains, not normalize the epoch or infer a process-global sequence.
        foreach (var (epoch, revision) in new[] { ("authority-before-restart", long.MaxValue), ("authority-after-restart", 0L) })
        {
            var expected = new SharpLinkGenerationInventory
            {
                EndpointEpoch = epoch,
                Revision = revision,
                Generations =
                [
                    new SharpLinkGenerationSnapshot
                    {
                        Descriptor = new SharpLinkGenerationDescriptor
                        {
                            Identity = new SharpLinkGenerationIdentity { CapabilityId = "search", GenerationId = "g12" },
                            WireIdentity = "wire-12",
                            GeneratedAbiIdentity = "application-owned-abi-identity",
                            ArtifactHash = "sha256:12",
                            ArtifactReference = "local://search/g12",
                            CompatibilityMetadata = "expand-before-drain"
                        },
                        State = SharpLinkGenerationState.Draining,
                        DiagnosticCode = "waiting-for-local-leases"
                    }
                ]
            };
            using var bytes = new PooledByteBufferWriter();
            codec.Serialize(in expected, bytes);
            var payload = new ReadOnlySequence<byte>(bytes.WrittenMemory);
            var actual = codec.Deserialize(in payload)
                ?? throw new InvalidOperationException("Generation inventory unexpectedly decoded as null.");
            if (actual.EndpointEpoch != epoch || actual.Revision != revision || actual.Generations.Length != 1)
                throw new InvalidOperationException("Generation inventory epoch/revision serialization changed the declaration.");
            var snapshot = actual.Generations[0];
            var descriptor = snapshot.Descriptor;
            if (descriptor.Identity.CapabilityId != "search" || descriptor.Identity.GenerationId != "g12" ||
                descriptor.WireIdentity != "wire-12" || descriptor.GeneratedAbiIdentity != "application-owned-abi-identity" ||
                descriptor.ArtifactHash != "sha256:12" || descriptor.ArtifactReference != "local://search/g12" ||
                descriptor.CompatibilityMetadata != "expand-before-drain" || snapshot.State != SharpLinkGenerationState.Draining ||
                snapshot.DiagnosticCode != "waiting-for-local-leases")
                throw new InvalidOperationException("Generation inventory codec lost compatibility or lifecycle metadata.");
        }

        var empty = new SharpLinkGenerationInventory { EndpointEpoch = "empty-authority", Revision = 1 };
        using var emptyBytes = new PooledByteBufferWriter();
        codec.Serialize(in empty, emptyBytes);
        var emptyPayload = new ReadOnlySequence<byte>(emptyBytes.WrittenMemory);
        var emptyActual = codec.Deserialize(in emptyPayload)
            ?? throw new InvalidOperationException("Empty generation inventory unexpectedly decoded as null.");
        if (emptyActual.EndpointEpoch != "empty-authority" || emptyActual.Revision != 1 || emptyActual.Generations.Length != 0)
            throw new InvalidOperationException("An empty complete generation declaration did not round-trip.");
        Console.WriteLine("GENERATION_CONTROL_SERIALIZATION_PASS full+empty+epoch-reset");
    }
}
