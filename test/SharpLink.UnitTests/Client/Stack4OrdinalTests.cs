using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

public sealed class Stack4OrdinalTests
{
    [Test]
    public void DirectMappingShouldMatchReferenceIncludingInconsistentCounts()
    {
        for (var length = -1; length <= 64; length++)
        {
            foreach (var excluded in new ulong[] { 0, 1, 5, ulong.MaxValue, 1UL << 63 })
            {
                for (var available = -1; available <= 66; available++)
                {
                    for (var target = -1; target <= 67; target++)
                    {
                        var expected = Reference(length, excluded, available, target);
                        var actual = EndpointSelectionKernel.SelectRandomIndex(length, excluded, available, target);
                        if (actual != expected)
                            throw new InvalidOperationException($"Mapping changed: {length}, {excluded}, {available}, {target}.");
                    }
                }
            }
        }
    }

    private static int Reference(int length, ulong excluded, int available, int target)
    {
        if (available <= 0 || target < 0 || target >= available)
            return -1;
        for (var index = 0; index < length; index++)
        {
            if ((excluded & (1UL << index)) != 0)
                continue;
            if (target-- == 0)
                return index;
        }
        return -1;
    }
}
