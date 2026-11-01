namespace SharpLink.Runtime;

// Exceptional transport only: preserve the exact raw outcome before async OCE conversion.
// This sealed marker is neither a task owner nor a public contract. Never flatten its payload.
internal sealed class PhysicalTaskFailureException : Exception
{
    internal PhysicalTaskFailureException(Exception rawException)
        : base("An independently owned physical task failed.", rawException ?? throw new ArgumentNullException(nameof(rawException)))
        => RawException = rawException;

    internal Exception RawException { get; }

    internal static PhysicalTaskFailureException From(Exception failure)
        => failure as PhysicalTaskFailureException ?? new PhysicalTaskFailureException(failure);

    internal static Exception? CombineRaw(Exception? first, Exception? next)
        => first is null ? next : next is null ? first : new AggregateException(first, next);

    internal static Exception GetRaw(Exception failure)
        => failure is PhysicalTaskFailureException physical ? physical.RawException : failure;
}
