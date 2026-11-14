using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

var folder = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var file = Path.Combine(folder, name.Name + ".dll");
    return File.Exists(file) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(file) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "SharpLink.Runtime.dll"));
var abstractions = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(folder, "SharpLink.Abstractions.dll"));
var options = Activator.CreateInstance(assembly.GetType("SharpLink.Runtime.BufferWriterPoolOptions", true)!);
var pool = Activator.CreateInstance(assembly.GetType("SharpLink.Runtime.SharpLinkBufferWriterPool", true)!, options);
var mailbox = assembly.GetType("SharpLink.Runtime.PreAdmissionStreamDispatcher", true)!;
var constructor = mailbox.GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).Single();
object?[] input = [pool, (Func<int, bool>)(_ => true), (Action<int>)(_ => { }), (Action)(() => { }), null, true, int.MaxValue];
object? last = null;
for (var i=0; i<4096; i++) last = constructor.Invoke(input);
var before = GC.GetAllocatedBytesForCurrentThread();
for (var i=0; i<32768; i++) last = constructor.Invoke(input);
var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
GC.KeepAlive(last);
((IDisposable)pool!).Dispose();
Console.WriteLine(JsonSerializer.Serialize(new
{
    iterations=32768, bytes, bytesPerMailboxWithCommonReflection=(double)bytes/32768,
    runtime=Environment.Version.ToString(),
    source=assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
}));
