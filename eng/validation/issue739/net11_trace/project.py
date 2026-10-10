"""Narrow marker overlay for the copied traditional consumer; never production code."""
import importlib.util
from pathlib import Path
import shutil

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[3]
NET11 = HERE.parent / "net11"
TRACE = HERE.parent / "trace"


def module(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    value = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(value)
    return value


pilot = module("issue739_net11_pilot", NET11 / "run_pilot.py")
summary = module("issue739_trace_summary", TRACE / "summarize.py")
require = pilot.require


def driver_source(original):
    edits = [
        ('        string transport = args[0], kind = args[1];',
         '''        string transport = args[0], kind = args[1];
        string? requestedTrace = Environment.GetEnvironmentVariable("ISSUE739_TRACE");
        if (requestedTrace is not ("0" or "1")) throw new InvalidOperationException("Explicit trace mode required");
        bool traceExpected = requestedTrace == "1";'''),
        ('        if (concurrency is not (1 or 32 or 128) || operations % concurrency != 0 || warmup % concurrency != 0)',
         '        if (kind != "add" || concurrency != 1 || operations != 131072 || warmup != 32768)'),
        ('        _ = Process.GetCurrentProcess().TotalProcessorTime;',
         '''        // Exercise both event payload paths outside measurement, even in untraced controls.
        string markerWarmupSample = args[4] + "-marker-warmup";
        Markers.Log.Start(markerWarmupSample);
        Markers.Log.Stop(markerWarmupSample, 0, 1);
        if (Markers.Log.IsEnabled() != traceExpected) throw new InvalidOperationException("Warmup marker provider state mismatch");
        _ = Process.GetCurrentProcess().TotalProcessorTime;'''),
        ('        var cpuStart = process.TotalProcessorTime;',
         '''        bool markerEnabledAtStart = Markers.Log.IsEnabled();
        if (markerEnabledAtStart != traceExpected) throw new InvalidOperationException("Start marker provider state mismatch");
        long markerStartTicks = Stopwatch.GetTimestamp();
        Markers.Log.Start(args[4]);
        var cpuStart = process.TotalProcessorTime;'''),
        ('        long received = service.Received - receivedStart;',
         '''        bool markerEnabledAtStop = Markers.Log.IsEnabled();
        if (markerEnabledAtStop != traceExpected) throw new InvalidOperationException("Stop marker provider state mismatch");
        Markers.Log.Stop(args[4], bytesEnd - bytesStart, operations);
        long markerStopTicks = Stopwatch.GetTimestamp();
        long received = service.Received - receivedStart;'''),
        ('            diagnostic = false, traceEnabled = false,',
         '''            diagnostic = true, traceEnabled = traceExpected, net11TraceDiagnostic = true,
            markerWarmupCompleted = true, markerEnabledAtStart, markerEnabledAtStop,
            markerStartTicks, markerStopTicks,
            markerProvider = "SharpLink-Issue739", markerSchema = "Start(sample);Stop(sample,bytes,operations);PID from event header",
            markerBoundary = "Start before CPU/GC/precise-byte baseline; Stop after endpoint snapshots; marker window wider than precise byte/time window; crossing ticks can straddle boundaries",
            corelibPath = typeof(object).Assembly.Location,
            corelibSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(object).Assembly.Location))).ToLowerInvariant(),'''),
    ]
    for old, new in edits:
        require(original.count(old) == 1, "Driver overlay anchor missing or ambiguous: " + old)
        original = original.replace(old, new)
    return original


def project(root, target, variant, names):
    overlays = pilot.make_copy(root, NET11, target, variant, names)
    relative = NET11.relative_to(root)
    source = target / relative / "Driver/Program.cs"
    old_hash = pilot.sha(source)
    source.write_text(driver_source(source.read_text()))
    markers = source.with_name("Markers.cs")
    shutil.copy2(HERE / "Driver/Markers.cs", markers)
    overlays[str(source.relative_to(target))] = {"originalSha256": old_hash, "projectedSha256": pilot.sha(source),
                                               "scope": "isolated fixed traditional consumer only"}
    overlays[str(markers.relative_to(target))] = {"projectedSha256": pilot.sha(markers), "scope": "isolated fixed traditional marker provider"}
    return overlays
