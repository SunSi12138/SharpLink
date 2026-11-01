using System.Reflection;

namespace SharpLink.Runtime;

// Cold registry admission only. Both dictionaries are host-owned reference-identity maps and
// their shared registry gate is held by the caller. No extra set/catalog/counter is maintained.
internal static class SharpLinkModuleAdmissionCapacity
{
    internal static int Count<TModule, TOperation>(
        Dictionary<Assembly, TModule> modules, Dictionary<Assembly, TOperation> operations)
    {
        var count = modules.Count;
        foreach (var entry in operations)
            if (!modules.ContainsKey(entry.Key)) count = checked(count + 1);
        return count;
    }
}
