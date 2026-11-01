namespace SharpLink.Server;

// One cold lexical workspace: each allocation is charged before creation; disposal
// clears every tracked collection root before its charge is returned. Tracking nodes
// are fixed owner controls covered by the corresponding collection header.
internal sealed class ServerMetadataWorkspace(ColdMetadataPreparation preparation) : IDisposable
{
    private Item? _head;
    internal ColdMetadataReservation Reserve(int count, ColdMetadataScope? scope = null)
    {
        var charge = preparation.ReserveCollection(count, scope);
        try { _head = new Item(charge, _head); return charge; }
        catch { charge.Dispose(); throw; }
    }
    internal void ClearBeforeRelease(Action clear)
    {
        if (_head is null) throw new InvalidOperationException("Reserve before assigning a workspace root.");
        _head.Clear = clear;
    }
    public void Dispose()
    {
        var item = _head;
        _head = null;
        while (item is not null)
        {
            var next = item.Next;
            item.Clear?.Invoke();
            item.Clear = null;
            item.Next = null;
            item.Charge.Dispose();
            item = next;
        }
    }
    private sealed class Item(ColdMetadataReservation charge, Item? next)
    {
        internal ColdMetadataReservation Charge { get; } = charge;
        internal Item? Next = next;
        internal Action? Clear;
    }
}
