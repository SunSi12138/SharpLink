using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

internal static class RollbackTestIsolation
{
    internal static void RemoveManifestFromCatalog(ISharpLinkGeneratedAssemblyManifest manifest)
    {
        var gateField = typeof(SharpLinkGeneratedAssemblyCatalog).GetField(
            "Gate",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new Exception("cannot find Manifest Catalog gate");
        var entriesField = typeof(SharpLinkGeneratedAssemblyCatalog).GetField(
            "Entries",
            BindingFlags.Static | BindingFlags.NonPublic) ?? throw new Exception("cannot find Manifest Catalog entries");
        var gate = (Lock)(gateField.GetValue(null) ?? throw new Exception("cannot read Manifest Catalog gate"));
        var entries = (List<WeakReference<ISharpLinkGeneratedAssemblyManifest>>)(entriesField.GetValue(null) ??
            throw new Exception("cannot read Manifest Catalog entries"));
        lock (gate)
        {
            for (var index = entries.Count - 1; index >= 0; index--)
            {
                if (entries[index].TryGetTarget(out var candidate) && ReferenceEquals(candidate, manifest))
                    entries.RemoveAt(index);
            }
        }
    }
}
