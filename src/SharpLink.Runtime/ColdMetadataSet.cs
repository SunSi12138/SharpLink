namespace SharpLink.Runtime;

// Validation-only host collection: charge precedes allocation; Dispose severs roots first.
internal sealed class ColdMetadataSet<T> : IDisposable
{
    private readonly HashSet<T> _set;
    private readonly ColdMetadataReservation _reservation;
    internal ColdMetadataSet(int capacity, ColdMetadataPreparation preparation,
        ColdMetadataScope? scope = null, IEqualityComparer<T>? comparer = null)
    {
        _reservation = preparation.ReserveCollection(capacity, scope);
        try { _set = new HashSet<T>(capacity, comparer); }
        catch { _reservation.Dispose(); throw; }
    }
    internal bool Add(T value) => _set.Add(value);
    public void Dispose() { _set.Clear(); _reservation.Dispose(); }
}
