using System.Reflection;

namespace SharpLink.UnitTests.Runtime;

public sealed class ColdMetadataCatalogTests
{
    [Test]
    [NotInParallel("generated-catalog")]
    public void ManifestCatalogShouldCopyReservedStorageWithoutCallingMetadataInsideGate()
    {
        var manifest = new CatalogManifest();
        SharpLinkGeneratedAssemblyCatalog.Register(manifest);
        var reads = manifest.OwnerReads;
        var authority = new ColdMetadataAuthority();
        var preparation = authority.CreateDomain().BeginPreparation();
        IReadOnlyList<ISharpLinkGeneratedAssemblyManifest>? snapshot = ColdMetadataCatalog.CaptureManifests(preparation);
        Ensure(manifest.OwnerReads == reads);
        Ensure(authority.Bytes == ColdMetadataAuthority.HeaderCharge * 2 +
            ColdMetadataAuthority.SlotCharge * (ulong)snapshot.Count);
        var callerOwned = SharpLinkGeneratedAssemblyCatalog.CreateSnapshot();
        Ensure(callerOwned.Count <= SharpLinkGeneratedAssemblyCatalog.SnapshotCapacity && manifest.OwnerReads == reads);
        snapshot = null;
        preparation.Dispose();
        Ensure(authority.Bytes == 0);
    }

    [Test]
    public void CatalogCopyShouldRejectUndersizedDestinationBeforeGateWork()
    {
        var rejected = false;
        try { SharpLinkGeneratedAssemblyCatalog.CopyLiveSnapshot(Span<ISharpLinkGeneratedAssemblyManifest>.Empty); }
        catch (ArgumentException) { rejected = true; }
        Ensure(rejected);
        rejected = false;
        try { SharpLinkGeneratedClusterRouteCatalog.CopyLiveSnapshot(Span<ISharpLinkGeneratedClusterRouteManifest>.Empty); }
        catch (ArgumentException) { rejected = true; }
        Ensure(rejected);
    }

    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Catalog cold ownership invariant failed."); }

    private sealed class CatalogManifest : ISharpLinkGeneratedAssemblyManifest
    {
        internal int OwnerReads;
        public int ApiVersion => SharpLinkGeneratedManifestVersions.Api;
        public int ProtocolVersion => SharpLinkGeneratedManifestVersions.Protocol;
        public string GeneratorVersion => "test";
        public Assembly OwnerAssembly
        {
            get
            {
                var gate = (Lock)typeof(SharpLinkGeneratedAssemblyCatalog)
                    .GetField("Gate", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
                Ensure(!gate.IsHeldByCurrentThread);
                OwnerReads++;
                return typeof(CatalogManifest).Assembly;
            }
        }
        public string CompileTimeDescriptor => "catalog-copy";
        public IReadOnlyList<SharpLinkGeneratedContractDescriptor> Contracts => [];
        public IReadOnlyList<SharpLinkGeneratedServiceDescriptor> Services => [];
        public IReadOnlyList<IRpcGeneratedCodecFactory> Codecs => [];
        public IReadOnlyList<string> Dependencies => [];
    }
}
