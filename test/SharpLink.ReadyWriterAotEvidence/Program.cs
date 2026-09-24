using System.Text.Json;
using SharpLink.Benchmarks;

// Exercise the same linked source under CoreCLR with reflection disabled and NativeAOT.
if (JsonSerializer.IsReflectionEnabledByDefault)
    throw new InvalidOperationException("The native evidence host must not use reflection serialization.");
if (args.Length == 1 && args[0] == "--ready-writer-self-test")
    await ReadyWriterChecks.RunAsync();
else if (args.Length > 0 && args[0] == "--ready-writer-evidence")
    await ReadyWriterEvidence.RunAsync(args[1..]);
else
    throw new ArgumentException("Use --ready-writer-self-test or --ready-writer-evidence with transport arguments.");
