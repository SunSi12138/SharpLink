using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Text.Json;

var path = Path.GetFullPath(args[0]);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var dependency = Path.Combine(Path.GetDirectoryName(path)!, name.Name + ".dll");
    return File.Exists(dependency) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(dependency) : null;
};
var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
var states = assembly.GetTypes().Where(type => type.IsValueType &&
    type.FullName!.Contains("SharpLinkServer+") &&
    (type.Name.Contains("Admission") || type.Name.Contains("DispatchRequestAsync") ||
     type.Name.Contains("ServiceSelection"))).Select(type => new
     {
         Type = type.FullName,
         Bytes = (int)typeof(Unsafe).GetMethod("SizeOf")!.MakeGenericMethod(type).Invoke(null, null)!,
         Fields = type.GetFields(fields).Select(field => new { field.Name, Type = field.FieldType.FullName }).ToArray()
     }).ToArray();
var classes = new[] { "SharpLink.Server.ServiceRegistration", "SharpLink.Server.ServerRetainedAdmissionPayload" }
    .Select(name => assembly.GetType(name, throwOnError: true)!).Select(type =>
{
    var sample = RuntimeHelpers.GetUninitializedObject(type);
    var roots = new object[1000];
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var index = 0; index < roots.Length; index++)
        roots[index] = RuntimeHelpers.GetUninitializedObject(type);
    var bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / roots.Length;
    var offsets = type.GetFields(fields).Select(field =>
    {
        var method = new DynamicMethod("FieldOffset", typeof(long), [typeof(object)], true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Castclass, type); il.Emit(OpCodes.Ldflda, field);
        il.Emit(OpCodes.Conv_I); il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Conv_I);
        il.Emit(OpCodes.Sub); il.Emit(OpCodes.Conv_I8); il.Emit(OpCodes.Ret);
        var offset = method.CreateDelegate<Func<object, long>>()(sample);
        return new { field.Name, Type = field.FieldType.FullName, Offset = offset };
    }).ToArray();
    GC.KeepAlive(roots);
    return new { Type = type.FullName, ManagedAllocationBytes = bytes, Fields = offsets };
}).ToArray();
var target = new object();
var weak = new WeakReference<object>[1000];
_ = new WeakReference<object>(target);
var start = GC.GetAllocatedBytesForCurrentThread();
for (var index = 0; index < weak.Length; index++) weak[index] = new WeakReference<object>(target);
var weakBytes = (GC.GetAllocatedBytesForCurrentThread() - start) / weak.Length;
GC.KeepAlive(weak); GC.KeepAlive(target);
Console.WriteLine(JsonSerializer.Serialize(new
{
    Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    ServerSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))),
    WeakReferenceManagedAllocationBytes = weakBytes,
    StateMachines = states,
    Classes = classes
}, new JsonSerializerOptions { WriteIndented = true }));
