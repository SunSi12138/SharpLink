namespace SharpLink.Abstractions;

/// <summary>Provides access to metadata for the currently executing server RPC.</summary>
public static class SharpLinkCallContext
{
    private static readonly AsyncLocal<SharpLinkCallContextSnapshot?> SCurrent = new();

    /// <summary>Gets the current call snapshot, or <see langword="null"/> outside server dispatch.</summary>
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
