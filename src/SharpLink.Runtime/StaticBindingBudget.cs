#if SHARPLINK_775_STATIC_SLICE
namespace SharpLink.Runtime;

// Shared by all physical Sessions of one client/server. Operation counts also bound
// fixed-size control frames; existing per-Session send-queue limits remain in force.
internal sealed class StaticBindingBudget
{
    internal const int BindingLimit = 128;
    internal const int OperationLimit = 64;
    private int _bindings;
    private int _operations;
    internal int Bindings => Volatile.Read(ref _bindings);
    internal int Operations => Volatile.Read(ref _operations);
    internal Lease? TryReserveBinding() => Reserve(ref _bindings, BindingLimit, false);
    internal Lease? TryReserveOperation() => Reserve(ref _operations, OperationLimit, true);

    private Lease? Reserve(ref int count, int limit, bool operation)
    {
        while (true)
        {
            var current = Volatile.Read(ref count);
            if (current >= limit) return null;
            if (Interlocked.CompareExchange(ref count, current + 1, current) == current)
                return new Lease(this, operation);
        }
    }

    internal sealed class Lease(StaticBindingBudget owner, bool operation) : IDisposable
    {
        private StaticBindingBudget? _owner = owner;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is not { } current) return;
            if (operation) Interlocked.Decrement(ref current._operations);
            else Interlocked.Decrement(ref current._bindings);
        }
    }
}
#endif

