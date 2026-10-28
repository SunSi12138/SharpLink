using System.Text.Json;
using Microsoft.Diagnostics.Tracing;

if (args.Length != 2) throw new ArgumentException("trace.nettrace new-output-directory");
var directory = args[1];
if (Directory.Exists(directory)) throw new IOException("Output exists");
Directory.CreateDirectory(directory);
using var output = new StreamWriter(Path.Combine(directory, "events.jsonl"));
using var source = new EventPipeEventSource(args[0]);
long count = 0;
double first = double.MaxValue, last = double.MinValue;
var providers = new Dictionary<string, long>();
void Record(TraceEvent e)
{
    count++;
    first = Math.Min(first, e.TimeStampRelativeMSec);
    last = Math.Max(last, e.TimeStampRelativeMSec);
    providers[e.ProviderName] = providers.GetValueOrDefault(e.ProviderName) + 1;
    // Keep runtime and our synthetic phase payload only. No network/client/secret providers.
    if (e.ProviderName is not ("Microsoft-Windows-DotNETRuntime" or "SharpLink-RpcCpu-Phase")) return;
    var payload = new Dictionary<string, string?>();
    foreach (var key in e.PayloadNames) payload[key] = e.PayloadByName(key)?.ToString();
    output.WriteLine(JsonSerializer.Serialize(new { provider = e.ProviderName, name = e.EventName,
        id = (int)e.ID, timeMs = e.TimeStampRelativeMSec, pid = e.ProcessID, tid = e.ThreadID, payload }));
}
// CLR events need its parser; Dynamic.All alone does not mean all runtime events.
source.Clr.All += Record;
source.Dynamic.All += e => { if (e.ProviderName == "SharpLink-RpcCpu-Phase") Record(e); };
source.Process();
File.WriteAllText(Path.Combine(directory, "coverage.json"), JsonSerializer.Serialize(new {
    censusScope = "selected CLR and SharpLink phase parser callbacks, not all raw EventPipe providers",
    parsedEvents = count, firstEventMs = first, lastEventMs = last, eventsLost = source.EventsLost, providers,
    parserAssembly = typeof(EventPipeEventSource).Assembly.FullName,
    kernelScheduling = "unavailable: EventPipe is not a context-switch trace"
}, new JsonSerializerOptions { WriteIndented = true }));

if (source.EventsLost != 0) throw new InvalidDataException($"Lost EventPipe events: {source.EventsLost}");
