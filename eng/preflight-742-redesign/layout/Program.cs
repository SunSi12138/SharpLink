using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
foreach (var root in args)
{
    Console.WriteLine($"ROOT {root}");
    var path = Path.Combine(root, "test/SharpLink.UnitTests/bin/Release/net10.0");
    var alc = new AssemblyLoadContext(root, isCollectible: true);
    alc.Resolving += (_, name) => { var p = Path.Combine(path, name.Name + ".dll"); return File.Exists(p) ? alc.LoadFromAssemblyPath(p) : null; };
    var runtime = alc.LoadFromAssemblyPath(Path.Combine(path, "SharpLink.Runtime.dll"));
    var client = alc.LoadFromAssemblyPath(Path.Combine(path, "SharpLink.Client.dll"));
    var lease = runtime.GetType("SharpLink.Runtime.StreamFlowController+ResolvedSendCreditLease")!;
    foreach (var t in new[] { lease, typeof(ValueTask), typeof(ValueTask<>).MakeGenericType(lease), typeof(AsyncValueTaskMethodBuilder<>).MakeGenericType(lease), typeof(AsyncValueTaskMethodBuilder) })
        Console.WriteLine($"SIZE {t} {Size(t)}");
    foreach (var asm in new[] {runtime, client})
        foreach (var raw in asm.GetTypes().Where(t => t.Name.StartsWith("<") && new[] {"PumpGeneratedOutboundStreamAsync", "SendClientStreamAsync", "AwaitResolvedSendCreditAsync", "AwaitPreCreditBudgetAndResolvedFlowCreditAsync", "AwaitPreCreditBudgetAndFlowCreditAsync", "AwaitPreCreditBudgetAndRetainedFlowCreditAsync", "SendStreamChunkKnownSizeWithCreditLeaseAsync", "SendStreamChunkKnownSizeAsync", "SendStreamChunkKnownSizeResolvedAsync", "SendClientStreamChunkKnownSizeAsync", "AwaitClientPreCreditBudgetAndFlowCreditAsync"}.Any(n => t.Name.StartsWith("<" + n + ">"))))
        {
            var t = raw.IsGenericTypeDefinition ? raw.MakeGenericType(Enumerable.Repeat(typeof(int), raw.GetGenericArguments().Length).ToArray()) : raw;
            Console.WriteLine($"STATE {t.Name} {Size(t)}");
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                Console.WriteLine($"  {f.Name}: {f.FieldType} {Size(f.FieldType)}");
        }
    alc.Unload();
}
static int Size(Type t) => (int)typeof(Sizer).GetMethod("Of")!.MakeGenericMethod(t).Invoke(null, null)!;
public static class Sizer { public static int Of<T>() => Unsafe.SizeOf<T>(); }
