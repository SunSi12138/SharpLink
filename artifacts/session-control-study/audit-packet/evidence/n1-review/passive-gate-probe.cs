using System.Buffers;
using System.Reflection;
using SharpLink.Abstractions;

var assemblyPath = Environment.GetEnvironmentVariable("N1_TEST_DLL")!;
var assembly = Assembly.LoadFrom(assemblyPath);
var gateType = assembly.GetType("SharpLink.N1.StaticBindEvidence.TransportFrameGate", throwOnError: true)!;
var ctor = gateType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Single();
var gate = ctor.Invoke(new object[] { (ProtocolV2FrameType)255, false });
var observe = gateType.GetMethod("Observe", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
var parameters = observe.GetParameters();
Console.WriteLine("observe params: " + string.Join(", ", parameters.Select(p => p.ParameterType.FullName + " " + p.Name)));
var payload = ReadOnlySequence<byte>.Empty;
for (var i = 1; i <= 513; i++)
{
    try
    {
        _ = observe.Invoke(gate, new object[] { "probe", true, default(ProtocolV2FrameHeader), payload });
    }
    catch (TargetInvocationException ex)
    {
        Console.WriteLine($"observe_threw_at={i} type={ex.InnerException?.GetType().FullName} message={ex.InnerException?.Message}");
        return 2;
    }
}
Console.WriteLine("observe_did_not_throw_at_513");
return 3;
