namespace SharpLink.Runtime;

internal static class SharedMemorySpin
{
    internal static void Wait(int count)
    {
        // There is no ring-cursor recheck between the old four-yield calls.
        // Batch the short profile spins, but keep large custom spins in short bursts.
        if (count is > 0 and <= 64)
        {
            Thread.SpinWait(count * 4);
            return;
        }

        for (var i = 0; i < count; i++)
            Thread.SpinWait(4);
    }
}
