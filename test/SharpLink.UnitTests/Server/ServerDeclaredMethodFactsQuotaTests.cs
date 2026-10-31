using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ServerDeclaredMethodFactsQuotaTests
{
    [Test]
    public void OnlyHeaderOnlyUnstartedContractsMayBeDiscardedBeforeSealing()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var unused = group.ReserveContract();
        group.DiscardContract(unused);
        Ensure(quota.Bytes == 0);
        var selected = group.ReserveContract();
        selected.SetMethodCount(0);
        try { group.DiscardContract(selected); }
        catch (InvalidOperationException)
        {
            Ensure(quota.Bytes == 256);
            group.Seal();
            group.Dispose();
            Ensure(quota.Bytes == 0);
            return;
        }
        throw new Exception("Count-assigned reservations cannot be discarded as unused staging.");
    }

    [Test]
    public void ServerCountLimitIncludesAllUnpublishedGroups()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        var groups = new List<ServerDeclaredMethodFactsGroupReservation>();
        try
        {
            for (var groupIndex = 0; groupIndex < 4; groupIndex++)
            {
                var group = quota.BeginGroup();
                groups.Add(group);
                for (var contract = 0; contract < 4; contract++)
                    group.ReserveContract().SetMethodCount(ServerDeclaredMethodFactsQuota.ContractMethodLimit);
            }
            Ensure(quota.Methods == ServerDeclaredMethodFactsQuota.ServerMethodLimit);
            using var extra = quota.BeginGroup();
            var reservation = extra.ReserveContract();
            ExpectCapacity(() => reservation.SetMethodCount(1));
            Ensure(quota.Methods == ServerDeclaredMethodFactsQuota.ServerMethodLimit);
        }
        finally
        {
            foreach (var group in groups) group.Dispose();
        }
        Ensure(quota.Methods == 0 && quota.Bytes == 0);
    }

    [Test]
    public void EmptyContractTraversalHitsRealGroupHeaderLimit()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        var tables = ServerDeclaredMethodFactsQuota.GroupByteLimit / ServerDeclaredMethodFactsQuota.TableCharge;
        for (ulong index = 0; index < tables; index++)
            group.ReserveContract().SetMethodCount(0);
        Ensure(quota.Methods == 0 && quota.Bytes == ServerDeclaredMethodFactsQuota.GroupByteLimit);
        ExpectCapacity(() => group.ReserveContract());
        group.Dispose();
        Ensure(quota.Bytes == 0);
    }

    [Test]
    public void ContractAndGroupCountLimitsAreCheckedBeforePreparation()
    {
        var quota = new ServerDeclaredMethodFactsQuota();
        using var group = quota.BeginGroup();
        for (var index = 0; index < 4; index++)
            group.ReserveContract().SetMethodCount(ServerDeclaredMethodFactsQuota.ContractMethodLimit);
        Ensure(quota.Methods == ServerDeclaredMethodFactsQuota.GroupMethodLimit);
        var extra = group.ReserveContract();
        ExpectCapacity(() => extra.SetMethodCount(1));
        Ensure(quota.Methods == ServerDeclaredMethodFactsQuota.GroupMethodLimit);
        group.Dispose();
        Ensure(quota.Bytes == 0 && quota.Methods == 0);
        using var another = quota.BeginGroup();
        var contract = another.ReserveContract();
        ExpectCapacity(() => contract.SetMethodCount(ServerDeclaredMethodFactsQuota.ContractMethodLimit + 1));
        Ensure(quota.Methods == 0 && quota.Bytes == 256);
    }

    [Test]
    public void EmptyTablesConsumeHeaderCapacityAndRollbackExactlyOnce()
    {
        var quota = new ServerDeclaredMethodFactsQuota(methodLimit: 0, byteLimit: 512);
        using var group = quota.BeginGroup();
        group.ReserveContract().SetMethodCount(0);
        group.ReserveContract().SetMethodCount(0);
        ExpectCapacity(() => group.ReserveContract());
        Ensure(quota.Bytes == 512 && quota.Methods == 0);
        group.Dispose();
        group.Dispose();
        Ensure(quota.Bytes == 0);
    }

    [Test]
    public void OldAndUnpublishedReplacementChargesOverlap()
    {
        var quota = new ServerDeclaredMethodFactsQuota(methodLimit: 1, byteLimit: 768);
        using var old = quota.BeginGroup();
        old.ReserveContract().SetMethodCount(1);
        using var replacement = quota.BeginGroup();
        var candidate = replacement.ReserveContract();
        ExpectCapacity(() => candidate.SetMethodCount(1));
        Ensure(quota.Methods == 1 && quota.Bytes == 640);
        replacement.Dispose();
        Ensure(quota.Methods == 1 && quota.Bytes == 384);
        old.Dispose();
        Ensure(quota.Methods == 0 && quota.Bytes == 0);
    }

    [Test]
    public void ChargeArithmeticHasHonestByteBoundaryAndOverflowChecks()
    {
        Ensure(ServerDeclaredMethodFactsQuota.CalculateCharge(0) == 256);
        Ensure(ServerDeclaredMethodFactsQuota.CalculateCharge(65_536) == 8UL * 1024 * 1024 + 256);
        // Arithmetic boundary only: this N is deliberately outside the contract method limit.
        var boundary = (ServerDeclaredMethodFactsQuota.ContractByteLimit - 256) / 128;
        Ensure(ServerDeclaredMethodFactsQuota.CalculateCharge(boundary) == ServerDeclaredMethodFactsQuota.ContractByteLimit);
        Ensure(ServerDeclaredMethodFactsQuota.CalculateCharge(boundary + 1) > ServerDeclaredMethodFactsQuota.ContractByteLimit);
        try { _ = ServerDeclaredMethodFactsQuota.CalculateCharge(ulong.MaxValue); }
        catch (OverflowException) { return; }
        throw new Exception("Expected checked charge overflow.");
    }

    private static void ExpectCapacity(Action action)
    {
        try { action(); }
        catch (ServerDeclaredMethodFactsException error)
        {
            Ensure(error.Code == SharpLinkAssemblyRegistrationErrorCode.CapacityExceeded);
            return;
        }
        throw new Exception("Expected facts capacity rejection.");
    }

    private static void Ensure(bool condition)
    { if (!condition) throw new Exception("Declared-method quota assertion failed."); }
}
