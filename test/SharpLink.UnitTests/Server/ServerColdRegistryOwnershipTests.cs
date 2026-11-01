using System.Collections.Frozen;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ServerColdRegistryOwnershipTests
{
    [Test]
    public void SupersededEmptyMapRemainsChargedUntilItsLastExactColdReaderReturns()
    {
        var authority = new ColdMetadataAuthority();
        using var preparation = authority.CreateDomain().BeginPreparation();
        var first = new ServerServiceSnapshot(FrozenDictionary<long, ServiceRegistration>.Empty,
            preparation, preparation.ReserveCollection(0));
        var registry = new ServerServiceModuleRegistry(first);
        ServerServiceSnapshot.Lease reader;
        lock (registry.Gate) reader = registry.CaptureServices();
        var second = new ServerServiceSnapshot(FrozenDictionary<long, ServiceRegistration>.Empty,
            preparation, preparation.ReserveCollection(0));
        ServerServiceSnapshot old;
        lock (registry.Gate)
        {
            old = registry.PublishServices(second);
            Ensure(!registry.IsCurrentServices(reader)); // Equal empty maps are not equal owners.
        }
        old.Dispose();
        preparation.Dispose();
        Ensure(authority.Bytes == 256 + 256 + 256);
        Ensure(reader.Services.Count == 0);
        reader.Dispose();
        reader.Dispose();
        Ensure(authority.Bytes == 256 + 256);
        ServerServiceSnapshot? final;
        lock (registry.Gate) final = registry.DetachServices();
        final!.Dispose();
        final.Dispose();
        Ensure(authority.Bytes == 0);
    }

    [Test]
    public void DetachedArrayOwnerRetainsItsChargeAcrossPreparationEndAndClearsBeforeRefund()
    {
        var authority = new ColdMetadataAuthority();
        using var preparation = authority.CreateDomain().BeginPreparation();
        var array = new ServiceRegistration[1];
        var charge = preparation.ReserveCollection(array.Length);
        var owner = new ServerDetachedServices(array, preparation, charge);
        preparation.Dispose();
        Ensure(authority.Bytes == 256 + 384 && ReferenceEquals(owner.Services, array));
        owner.Dispose();
        owner.Dispose();
        Ensure(authority.Bytes == 0 && array[0] is null);
        try { _ = owner.Services; }
        catch (ObjectDisposedException) { return; }
        throw new Exception("Detached cleanup ownership must be one-shot.");
    }

    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Cold registry owner assertion failed."); }
}
