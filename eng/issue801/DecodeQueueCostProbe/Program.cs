using System.Diagnostics;
using System.Reflection;
using System.Text.Json;

if (args.Length != 2)
    throw new ArgumentException("Usage: <baseline|reserved> <output-json-path>");

const int Warmups = 50;
const int Samples = 300;
var mode = args[0];
var path = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(path)!);

var assembly = Assembly.Load("SharpLink.Server");
var type = assembly.GetType("SharpLink.Server.ServerDecodeExecutor", throwOnError: true)!;
var ctor = type.GetConstructor(
    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
    binder: null,
    types: [typeof(int), typeof(int)],
    modifiers: null) ?? throw new InvalidOperationException("Could not find decode executor constructor");

var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
var fields = type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
var compatibilityFields = fields.Where(f => f.Name.Contains("compatibility", StringComparison.OrdinalIgnoreCase))
    .Select(f => f.Name).OrderBy(s => s, StringComparer.Ordinal).ToArray();
var compatibilityMethods = methods.Where(m => m.Name.Contains("Compatibility", StringComparison.Ordinal))
    .Select(m => m.Name).OrderBy(s => s, StringComparer.Ordinal).ToArray();
var schedulerKey = type.GetField("s_compatibilitySchedulingKey", BindingFlags.NonPublic | BindingFlags.Static);

var argsArray = new object?[] { 1, 4 };
var times = new long[Samples];
var sampledBytes = new long[Samples];
var stopwatch = Stopwatch.StartNew();

for (var i = -Warmups; i < Samples; i++)
{
    // Do not count the fixed reflection argument array; it is reused for both builds.
    var beforeBytes = GC.GetAllocatedBytesForCurrentThread();
    var startTicks = Stopwatch.GetTimestamp();
    var executor = ctor.Invoke(argsArray);
    var elapsed = Stopwatch.GetElapsedTime(startTicks).Ticks;
    var allocated = GC.GetAllocatedBytesForCurrentThread() - beforeBytes;
    if (i >= 0)
    {
        sampledBytes[i] = allocated;
        times[i] = elapsed;
    }
    await ((IAsyncDisposable)executor).DisposeAsync().ConfigureAwait(false);
}
Array.Sort(sampledBytes);
Array.Sort(times);
var report = new
{
    Mode = mode,
    DotnetRuntime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    Os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    Base = "dev:eb99fe887cf2129d9b88441245ca0a4a6406b6c2",
    ConstructorAllocationPerCurrentThreadBytes = new
    {
        Samples,
        P10 = sampledBytes[Samples / 10],
        Median = sampledBytes[Samples / 2],
        P90 = sampledBytes[Samples * 9 / 10],
        Mean = sampledBytes.Average(x => (double)x)
    },
    ConstructorTicks = new
    {
        P10 = times[Samples / 10],
        Median = times[Samples / 2],
        P90 = times[Samples * 9 / 10]
    },
    InstanceFieldCount = fields.Length,
    CompatibilityInstanceFields = compatibilityFields,
    CompatibilityMethods = compatibilityMethods,
    LegacyStaticSchedulingKeyExists = schedulerKey is not null,
    ElapsedSeconds = stopwatch.Elapsed.TotalSeconds,
    Limitations = "Reflection constructor timing is noisy; worker startup and ThreadPool scheduling influence samples. This does not estimate real RPC throughput, request latency or retained heap. Samples cover constructor allocation only."
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(path, json + Environment.NewLine);
Console.WriteLine(json);
