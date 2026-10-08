using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;

var directory = Path.GetFullPath(args.Single());
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var path = Path.Combine(directory, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
var roots = new[]
{
    (Assembly: "SharpLink.Client", Type: "SharpLink.Client.ClientConnection", Method: "SendClientStreamAsync"),
    (Assembly: "SharpLink.Runtime", Type: "SharpLink.Runtime.RpcSession", Method: "PumpGeneratedOutboundStreamAsync")
};
var reports = roots.Select(root =>
{
    var path = Path.Combine(directory, root.Assembly + ".dll");
    var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
    var owner = assembly.GetType(root.Type, throwOnError: true)!;
    var method = owner.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method => method.Name == root.Method && method.IsGenericMethodDefinition);
    var state = method.GetCustomAttribute<AsyncStateMachineAttribute>()!.StateMachineType;
    if (state.IsGenericTypeDefinition)
        state = state.MakeGenericType(typeof(byte[]));
    if (!state.IsValueType || state.ContainsGenericParameters)
        throw new InvalidOperationException("Expected the real Release byte[] root state machine.");
    return new
    {
        root = root.Method,
        closedMethod = method.MakeGenericMethod(typeof(byte[])).ToString(),
        stateMachine = state.ToString(),
        managedSize = SizeOf(state),
        assembly = assembly.FullName,
        assemblyPath = path,
        assemblySha256 = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))),
        fields = state.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(field => field.MetadataToken)
            .Select(field => new { name = field.Name, type = field.FieldType.ToString(),
                managedSize = SizeOf(field.FieldType) }).ToArray()
    };
}).ToArray();
var runtimeAssembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(directory, "SharpLink.Runtime.dll"));
var sessionType = runtimeAssembly.GetType("SharpLink.Runtime.RpcSession", throwOnError: true)!;
var flowType = runtimeAssembly.GetType("SharpLink.Runtime.StreamFlowController", throwOnError: true)!;
var leaseType = flowType.GetNestedType("ResolvedSendCreditLease", BindingFlags.Public | BindingFlags.NonPublic)!;
var leaseMethodNames = new[]
{
    "SendClientStreamChunkResolvedAsync", "SendGeneratedStreamChunkResolvedAsync",
    "SendUnsizedStreamChunkResolvedAsync", "SendStreamChunkKnownSizeResolvedAsync",
    "AwaitPreCreditBudgetAndRetainedFlowCreditAsync"
};
var leaseMethods = leaseMethodNames.Select(name =>
{
    var method = sessionType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Single(method => method.Name == name);
    var parameter = method.GetParameters().Single(parameter => parameter.Name == "creditLease");
    return new
    {
        method = name,
        leaseParameter = new
        {
            name = parameter.Name,
            type = parameter.ParameterType.ToString(),
            byRef = parameter.ParameterType.IsByRef,
            isIn = parameter.IsIn,
            requiredModifiers = parameter.GetRequiredCustomModifiers().Select(type => type.ToString()).ToArray(),
            attributes = parameter.GetCustomAttributesData().Select(attribute => attribute.AttributeType.ToString()).ToArray()
        }
    };
}).ToArray();
Console.WriteLine(JsonSerializer.Serialize(new
{
    runtime = Environment.Version.ToString(),
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    dynamicCodeSupported = RuntimeFeature.IsDynamicCodeSupported,
    roots = reports,
    lease = new
    {
        type = leaseType.ToString(),
        managedSize = SizeOf(leaseType),
        fields = leaseType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .OrderBy(field => field.MetadataToken)
            .Select(field => new { name = field.Name, type = field.FieldType.ToString(), managedSize = SizeOf(field.FieldType) }).ToArray()
    },
    leaseMethods,
    boundary = "Managed JIT layout of the actual Release assemblies closed over byte[]. This is not NativeAOT layout or an allocation/throughput result."
}, new JsonSerializerOptions { WriteIndented = true }));

static int SizeOf(Type type)
{
    var method = new DynamicMethod("ManagedSize", typeof(int), Type.EmptyTypes, typeof(Program).Module, true);
    var il = method.GetILGenerator();
    il.Emit(OpCodes.Sizeof, type);
    il.Emit(OpCodes.Ret);
    return ((Func<int>)method.CreateDelegate(typeof(Func<int>)))();
}
