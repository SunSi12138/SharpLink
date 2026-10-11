using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

/// <summary>
/// BDN compares ordinary *production* null hooks against deliberate enabled no-op hooks.
/// The null condition is the actual default; this is NOT a no-hook-source ablation.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 9)]
public class ServerAdmissionTestHookPathBenchmarks
{
    private BenchmarkEnvironment _environment = null!;
    private SharpLinkServer _server = null!;

    [Params(false, true)]
    public bool InstallNoOpTestHooks { get; set; }

    [GlobalSetup]
    public async Task Setup()
    {
        _environment = await BenchmarkEnvironment.CreateAsync(
            configureBuiltServer: server =>
                server.EnableAdmissionControl(options => options.Global.UseConcurrency(1024)));
        _server = (SharpLinkServer)_environment.Server;
        if (!InstallNoOpTestHooks &&
            System.Environment.GetEnvironmentVariable("AUDIT_TEST_STATE_OUTPUT") is { Length: > 0 } evidenceFile)
        {
            ClientServerTestStateInspector.Capture(_environment, evidenceFile);
        }
        if (InstallNoOpTestHooks)
        {
            SharpLinkServer.AfterAdmissionPublicationReadForTests = static (_, _, _) => { };
            SharpLinkServer.AfterAdmissionCaptureForTests = static (_, _, _) => { };
        }
    }

    [GlobalCleanup]
    public async Task Cleanup()
    {
        SharpLinkServer.AfterAdmissionPublicationReadForTests = null;
        SharpLinkServer.AfterAdmissionCaptureForTests = null;
        await _environment.DisposeAsync();
    }

    [Benchmark]
    public long CaptureAndReleaseRealAdmissionProgram()
    {
        var publication = _server.CaptureAdmissionProgramForTests(42)
            ?? throw new InvalidOperationException("Admission program was unexpectedly disabled.");
        try
        {
            return publication.GenerationId;
        }
        finally
        {
            publication.ReleaseUse();
        }
    }
}

/// <summary>Build/dispose cost with the normal Client test hook absent versus explicitly configured.</summary>
[MemoryDiagnoser]
[SimpleJob(RunStrategy.Throughput, launchCount: 1, warmupCount: 3, iterationCount: 9)]
public class ClientTestHookBuildBenchmarks
{
    private static readonly Func<CancellationToken, ValueTask> NoOp =
        static _ => ValueTask.CompletedTask;

    [Params(false, true)]
    public bool ConfigureTestHook { get; set; }

    [Benchmark]
    public async Task BuildAndDisposeUnconnectedClientAsync()
    {
        // No ConnectAsync: no network/dial variance is included in this construction measurement.
        var builder = SharpClientBuilder.Create()
            .UseTcp("127.0.0.1", 1)
            .DisableRequestTimeout();
        if (ConfigureTestHook)
            builder.UseBeforeReadyPublicationTestHook(NoOp);

        var client = builder.Build();
        await client.DisposeAsync().ConfigureAwait(false);
    }
}

internal static class ClientServerTestStateInspector
{
    internal static void Capture(BenchmarkEnvironment environment, string file)
    {
            var client = (SharpLinkClient)environment.Client;
            var server = (SharpLinkServer)environment.Server;
            var types = new[]
            {
                (Type: typeof(SharpLinkClient), Instance: (object?)client),
                (Type: typeof(SharpClientBuilder), Instance: (object?)null),
                (Type: typeof(SharpLinkServer), Instance: (object?)server),
                (Type: typeof(SharpLinkServerBuilder), Instance: (object?)null),
                (Type: typeof(AdmissionProgram), Instance: (object?)null),
                (Type: typeof(ServerCallCancellationState), Instance: (object?)null),
                (Type: typeof(SharpLinkAdmissionController), Instance: (object?)null)
            };
            var fields = types.SelectMany(item => item.Type
                .GetFields(BindingFlags.Public | BindingFlags.NonPublic |
                           BindingFlags.Instance | BindingFlags.Static |
                           BindingFlags.DeclaredOnly)
                .Where(field => field.Name.Contains("test", StringComparison.OrdinalIgnoreCase) ||
                                field.Name.Contains("hook", StringComparison.OrdinalIgnoreCase) ||
                                field.Name.Contains("probe", StringComparison.OrdinalIgnoreCase))
                .Select(field => new
                {
                    type = item.Type.FullName,
                    name = field.Name,
                    staticField = field.IsStatic,
                    instanceField = !field.IsStatic,
                    managedReference = !field.FieldType.IsValueType,
                    fieldType = field.FieldType.FullName,
                    observedOnNormalRuntime = field.IsStatic || item.Instance is not null,
                    isNullInNormalRuntime = (field.IsStatic || item.Instance is not null) &&
                                            field.GetValue(field.IsStatic ? null : item.Instance) is null
                })).OrderBy(x => x.type).ThenBy(x => x.name).ToArray();
            var payload = new
            {
                runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                timestampUtc = DateTimeOffset.UtcNow,
                kind = "observed normal constructed/connected production Client and Server",
                fields,
                notes = new[]
                {
                    "Field inventory is actual reflection, not measured per-field heap bytes.",
                    "A null delegate avoids callback invocation but the reference slot remains in the object/type.",
                    "The BenchmarkDotNet enabled-vs-disabled hook trials are not a compiler ablation.",
                    "Measure allocation B/op and ns/op in the raw BDN JSON; do not infer causal field-size savings."
                }
            };
            await File.WriteAllTextAsync(file,
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"TEST-STATE-FIELDS: {fields.Length}; normal runtime non-null hooks: " +
                              fields.Count(f => f.observedOnNormalRuntime && !f.isNullInNormalRuntime));
            foreach (var field in fields)
                Console.WriteLine($"FIELD: {field.type}.{field.name} static={field.staticField} " +
                                  $"observed={field.observedOnNormalRuntime} null={field.isNullInNormalRuntime}");
    }
}
