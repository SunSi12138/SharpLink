namespace SharpLink.Client;

internal sealed class SharpLinkClientStreamModuleLeaseOwner(SharpLinkDynamicModuleLease lease) : IDisposable
{
    private int _claimed;

    internal SharpLinkDynamicModuleLease TakeLease()
    {
        if (Interlocked.CompareExchange(ref _claimed, 1, 0) != 0)
            throw new InvalidOperationException("The dynamic client-stream producer lease was already claimed.");
        return lease;
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _claimed, 2, 0) == 0)
            lease.Dispose();
    }
}

internal static class SharpLinkClientStreamModuleLeaseContext
{
    private static readonly AsyncLocal<SharpLinkClientStreamModuleLeaseOwner?> CurrentOwner = new();

    internal static SharpLinkClientStreamModuleLeaseOwner? Current => CurrentOwner.Value;

    internal static Scope Push(SharpLinkClientStreamModuleLeaseOwner owner)
    {
        var previous = CurrentOwner.Value;
        CurrentOwner.Value = owner;
        return new Scope(previous);
    }

    internal readonly struct Scope(SharpLinkClientStreamModuleLeaseOwner? previous) : IDisposable
    {
        public void Dispose() => CurrentOwner.Value = previous;
    }
}
