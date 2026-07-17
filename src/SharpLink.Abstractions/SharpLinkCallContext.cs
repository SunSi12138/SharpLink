namespace SharpLink.Abstractions;

public static class SharpLinkCallContext
{
    private static readonly AsyncLocal<SharpLinkCallContextSnapshot?> SCurrent = new();

    public static SharpLinkCallContextSnapshot? Current => SCurrent.Value;

    internal static Scope Push(SharpLinkCallContextSnapshot? snapshot)
    {
        var previous = SCurrent.Value;
        SCurrent.Value = snapshot;
        return new Scope(previous);
    }

    internal struct Scope(SharpLinkCallContextSnapshot? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            SCurrent.Value = previous;
        }
    }
}
