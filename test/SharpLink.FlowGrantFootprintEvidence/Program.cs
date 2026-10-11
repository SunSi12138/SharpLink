using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static partial class Program
{
    private const string CommonControl = "c7ba1768f31ce7219dc438dc6009347de6806ed5";
    private const int Count = 10_000;
    private const int Rounds = 5;
    private const int ItemBytes = 16;
    private const int GrantQuantum = 4096;
    private const int PoolStreams = 128;
    private const int PoolControllers = 64;
    private static readonly SharpLinkRuntimeOptions Defaults = new();
    private static readonly Exception Terminal = new InvalidOperationException("footprint connection cleanup");
    private static StreamWriter? _output;

    private static int Main(string[] args)
    {
        try
        {
            Verify(args);
            var path = Path.GetFullPath(args[6]);
            Require(!File.Exists(path), "refusing to overwrite a previous sample");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            _output = writer;
            Metadata("schema", "sharplink-send-grant-footprint-v1");
            Metadata("classification", "current-thread-allocation-appendix-not-RPC-CPU-or-retained-heap");
            Metadata("mode", ModeIdentity.Mode);
            Metadata("baselineSha", ModeIdentity.BaselineSha);
            Metadata("subjectSha", ModeIdentity.SubjectSha);
            Metadata("subjectCommitTreeSha", ModeIdentity.SubjectCommitTreeSha);
            Metadata("runtimeSourceTreeSha", ModeIdentity.RuntimeSourceTreeSha);
            Metadata("runtimeSourceContentSha256", ModeIdentity.RuntimeSourceContentSha256);
            Metadata("measurementRecipeSha", ModeIdentity.MeasurementRecipeSha);
            Metadata("measurementSourceSha256", ModeIdentity.MeasurementSourceSha256);
            Metadata("controllerSourceSha256", ModeIdentity.ControllerSourceSha256);
            Metadata("grantSourceSha256", ModeIdentity.GrantSourceSha256);
            Metadata("runtimePopulation", args[5]);
            Metadata("runtime", RuntimeInformation.FrameworkDescription);
            Metadata("architecture", RuntimeInformation.ProcessArchitecture.ToString());
            Metadata("processArchitecture", RuntimeInformation.ProcessArchitecture.ToString());
            Metadata("os", RuntimeInformation.OSDescription);
            Metadata("dynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported.ToString().ToLowerInvariant());
            Metadata("dynamicCodeCompiled", RuntimeFeature.IsDynamicCodeCompiled.ToString().ToLowerInvariant());
#if FLOW_FOOTPRINT_NATIVE_AOT
            Require(args[5] == "nativeaot" && !RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled,
                "actual NativeAOT execution identity mismatch");
            Metadata("builtForNativeAot", "true");
            Metadata("measuredBinarySha256", Hash(Environment.ProcessPath!));
            Metadata("runtimeAssemblySha256", "embedded-in-native-executable");
#else
            Require((args[5] is "jit-pgo0" or "jit-pgo1") && RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled,
                "actual JIT execution identity mismatch");
            Metadata("builtForNativeAot", "false");
            Metadata("measuredBinarySha256", Hash(typeof(Program).Assembly.Location));
            Metadata("runtimeAssemblySha256", Hash(typeof(StreamFlowController).Assembly.Location));
#endif
#if FLOW_EVIDENCE_B2
            Metadata("usesProductionSendCreditLease", "true");
#else
            Metadata("usesProductionSendCreditLease", "false");
#endif
            foreach (var key in new[] { "DOTNET_TieredPGO", "DOTNET_TieredCompilation", "DOTNET_ReadyToRun", "DOTNET_PROCESSOR_COUNT",
                         "COMPlus_TieredPGO", "COMPlus_TieredCompilation", "COMPlus_ReadyToRun" })
                Metadata(key, Environment.GetEnvironmentVariable(key) ?? "UNSET");
            Metadata("iterations", Count.ToString(CultureInfo.InvariantCulture));
            Metadata("rounds", Rounds.ToString(CultureInfo.InvariantCulture));
            Metadata("poolControllers", PoolControllers.ToString(CultureInfo.InvariantCulture));
            Metadata("poolStreamsPerController", PoolStreams.ToString(CultureInfo.InvariantCulture));
            Metadata("encodedItemCreditBytes", ItemBytes.ToString(CultureInfo.InvariantCulture));
            Metadata("candidateGrantQuantumBytes", GrantQuantum.ToString(CultureInfo.InvariantCulture));
            Metadata("streamWindowBytes", Defaults.FlowControl.StreamReceiveWindowBytes.ToString(CultureInfo.InvariantCulture));
            Metadata("connectionWindowBytes", Defaults.FlowControl.ConnectionReceiveWindowBytes.ToString(CultureInfo.InvariantCulture));
            Metadata("maxFramePayloadBytes", Defaults.Protocol.MaxFramePayloadBytes.ToString(CultureInfo.InvariantCulture));
            Metadata("maxConcurrentStreams", Defaults.Protocol.MaxConcurrentStreamsPerConnection.ToString(CultureInfo.InvariantCulture));
            Metadata("counterScope", "GC.GetAllocatedBytesForCurrentThread; no async waits; arrays/delegates/checks/reporting outside counters except passing constant-message assertions");
            Metadata("nonClaims", "not retained heap/RSS/native memory/non-current-thread allocation/CPU/RPC throughput");
            Metadata("baselineNoOps", "disable-unused-grant;dispose-empty-grant;dispose-unused-grant have no baseline lease operation");
            Metadata("warmup", "one unreported complete round with 32 primary controllers; no timing or tier-stability claim");
            writer.WriteLine("phase\tround\titerations\ttotal_allocated_bytes\tallocated_bytes_per_iteration");
            RunRound(32, -1);
            for (var round = 0; round < Rounds; round++)
            {
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
                GC.WaitForPendingFinalizers();
                RunRound(Count, round);
            }
            Metadata("status", "passed");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { _output = null; }
    }

    private static void Verify(string[] args)
    {
        Require(args.Length == 7, "Usage: <mode> <baseline-sha> <subject-sha> <measurement-sha256> <src-tree-sha> <jit-pgo0|jit-pgo1|nativeaot> <output.tsv>");
        Require(ModeIdentity.BaselineSha == CommonControl && args[1] == CommonControl, "common correctness control mismatch");
        Require(args[0] == ModeIdentity.Mode && args[2] == ModeIdentity.SubjectSha &&
                args[3] == ModeIdentity.MeasurementSourceSha256 && args[4] == ModeIdentity.RuntimeSourceTreeSha,
                "CLI cannot relabel compiled source identity");
#if FLOW_EVIDENCE_B2
        Require(ModeIdentity.Mode == "B2-grant-4096", "B2 harness adapter was not compiled");
        ValidateSha(ModeIdentity.GrantSourceSha256, 64);
#else
        Require(ModeIdentity.Mode == "common-correctness-control" && ModeIdentity.SubjectSha == CommonControl &&
                ModeIdentity.GrantSourceSha256 == "ABSENT", "baseline must use its real keyed API");
#endif
        ValidateSha(ModeIdentity.SubjectSha, 40);
        ValidateSha(ModeIdentity.SubjectCommitTreeSha, 40);
        ValidateSha(ModeIdentity.RuntimeSourceTreeSha, 40);
        ValidateSha(ModeIdentity.RuntimeSourceContentSha256, 64);
        ValidateSha(ModeIdentity.MeasurementRecipeSha, 40);
        ValidateSha(ModeIdentity.MeasurementSourceSha256, 64);
        ValidateSha(ModeIdentity.ControllerSourceSha256, 64);
        Require(Defaults.FlowControl.StreamReceiveWindowBytes == (1 << 20) &&
                Defaults.FlowControl.ConnectionReceiveWindowBytes == (16 << 20) &&
                Defaults.Protocol.MaxFramePayloadBytes == (4 << 20) &&
                Defaults.Protocol.MaxConcurrentStreamsPerConnection == 1024, "production defaults changed");
    }

    private static StreamFlowController NewController() => new(
        Defaults.FlowControl.StreamReceiveWindowBytes, Defaults.FlowControl.ConnectionReceiveWindowBytes,
        Defaults.Protocol.MaxFramePayloadBytes, Defaults.Protocol.MaxConcurrentStreamsPerConnection);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void Measure(string phase, int round, int count, Action body)
    {
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        body();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(thread == Environment.CurrentManagedThreadId && allocated >= 0, "counter thread or allocation delta invalid");
        GC.KeepAlive(body);
        if (round < 0) return;
        _output!.WriteLine(string.Join("\t", phase, round, count, allocated,
            ((double)allocated / count).ToString("R", CultureInfo.InvariantCulture)));
    }

    private static void AssertSettled(StreamFlowController controller)
        => Require(controller.SendConnectionCredit == Defaults.FlowControl.ConnectionReceiveWindowBytes &&
                   controller.ActiveSendStreamCount == 0 && controller.RetainedSendStreamCount == 0,
                   "credit, active stream or tombstone did not settle");
    private static void ValidateSha(string value, int length)
    {
        Require(value.Length == length, "missing compiled source hash");
        foreach (var c in value) Require(Uri.IsHexDigit(c), "invalid compiled source hash");
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static void Metadata(string key, string value)
        => _output!.WriteLine("#" + key + "\t" + value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
}
