using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SharpLink.Runtime;

namespace SharpLink.Benchmarks;

internal static class Program
{
    private const string Baseline = "f8ba89121c87a6410a0139c44d7db30eab877f41";
    private const string BaselineController = "7694add9ffc1d858c80f0aa8f1f8815717068b8ff7181b86998672e68bfe60e2";
    private const string SplitController = "a9cad11d9f80d8aedce4d412ada02ade32cd8048e0cfe133ad469d029eaf658d";
    private const int Count = 10_000;
    private const int Rounds = 5;
    private static readonly SharpLinkRuntimeOptions Defaults = new();
    private static readonly Exception Terminal = new InvalidOperationException("footprint terminal cleanup");
    private static StreamWriter? _output;

    public static int Main(string[] args)
    {
        try
        {
            Verify(args);
            var output = Path.GetFullPath(args[3]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            using var writer = new StreamWriter(output, false, new UTF8Encoding(false)) { AutoFlush = true };
            _output = writer;
            Metadata("schema", "sharplink-controller-footprint-v1");
            Metadata("classification", "current-thread-managed-allocation-appendix-not-RPC-or-retained-heap");
            Metadata("mode", ModeIdentity.Mode);
            Metadata("baselineSha", ModeIdentity.BaselineSha);
            Metadata("candidateSha", ModeIdentity.CandidateSha);
            Metadata("runtimeSourceTreeSha", ModeIdentity.RuntimeSourceTreeSha);
            Metadata("measurementSourceSha", ModeIdentity.MeasurementSourceSha);
            Metadata("controllerSourceSha256", ModeIdentity.ControllerSourceSha256);
            Metadata("runtime", RuntimeInformation.FrameworkDescription);
            Metadata("architecture", RuntimeInformation.ProcessArchitecture.ToString());
            Metadata("frameworkDynamicCode", RuntimeFeature.IsDynamicCodeSupported.ToString());
#if FLOW_FOOTPRINT_NATIVE_AOT
            Require(!RuntimeFeature.IsDynamicCodeSupported && !RuntimeFeature.IsDynamicCodeCompiled, "AOT identity");
            Metadata("builtForNativeAot", "true");
            Metadata("measuredBinarySha256", Hash(Environment.ProcessPath!));
#else
            Require(RuntimeFeature.IsDynamicCodeSupported && RuntimeFeature.IsDynamicCodeCompiled, "JIT identity");
            Metadata("builtForNativeAot", "false");
            Metadata("measuredBinarySha256", Hash(typeof(Program).Assembly.Location));
            Metadata("runtimeAssemblySha256", Hash(typeof(StreamFlowController).Assembly.Location));
#endif
            Metadata("iterations", Count.ToString(CultureInfo.InvariantCulture));
            Metadata("rounds", Rounds.ToString(CultureInfo.InvariantCulture));
            Metadata("windows", $"{Defaults.FlowControl.StreamReceiveWindowBytes}/{Defaults.FlowControl.ConnectionReceiveWindowBytes}");
            Metadata("maxFramePayloadBytes", Defaults.Protocol.MaxFramePayloadBytes.ToString(CultureInfo.InvariantCulture));
            Metadata("scope", "GC.GetAllocatedBytesForCurrentThread; arrays/delegates/reporting and async waiter observation excluded");
            Metadata("waiterScope", "return/admit includes caller-thread wake scheduling; callback-thread allocations excluded");
            Metadata("retention", "allocated bytes are not retained heap, RSS, native memory or pool backing capacity");
            writer.WriteLine("phase\tround\titerations\ttotal_allocated_bytes\tallocated_bytes_per_operation");
            RunRound(32, -1); // Warm methods, exception singleton and callback machinery outside every sample.
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

    private static void RunRound(int count, int round)
    {
        // Instrumentation containers and delegates are allocated before each counter snapshot.
        var controllers = new StreamFlowController[count];
        var waits = new ValueTask[count];
        var locks = new Lock[count];
        var window = Defaults.FlowControl.StreamReceiveWindowBytes;
        Measure("one-System.Threading.Lock-object-constructor", round, count, () =>
        {
            for (var i = 0; i < count; i++) locks[i] = new Lock();
        });
        GC.KeepAlive(locks);
        Measure("controller-constructor", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i] = NewController();
        });
        Measure("first-send-state-and-dictionary-storage-reserve-full-stream-window", round, count, () =>
        {
            for (var i = 0; i < count; i++)
                Require(controllers[i].TryAcquireSendCredit(1, 0, window), "initial reservation");
        });
        Measure("exhausted-stream-nonallocating-probe", round, count, () =>
        {
            for (var i = 0; i < count; i++)
                Require(!controllers[i].TryAcquireSendCredit(1, 0, 1), "stream should be exhausted");
        });
        Measure("exhausted-stream-async-credit-waiter-creation", round, count, () =>
        {
            for (var i = 0; i < count; i++)
            {
                waits[i] = controllers[i].AcquireSendCreditAsync(1, 0, 1, CancellationToken.None);
                Require(!waits[i].IsCompleted, "waiter unexpectedly completed");
            }
        });
        Measure("return-original-unsent-credit-and-admit-waiter", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].ReturnUnsentCredit(1, 0, window);
        });
        // Continuations execute on other threads. Join outside allocation samples.
        for (var i = 0; i < count; i++) waits[i].GetAwaiter().GetResult();
        Measure("return-waiter-owned-one-byte", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].ReturnUnsentCredit(1, 0, 1);
        });
        Measure("complete-send-stream-and-retain-one-pooled-state", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].CompleteSendStream(1, 0);
        });
        for (var i = 0; i < count; i++)
            Require(controllers[i].SendConnectionCredit == Defaults.FlowControl.ConnectionReceiveWindowBytes &&
                    controllers[i].ActiveSendStreamCount == 0 && controllers[i].RetainedSendStreamCount == 0,
                    "send cleanup did not settle");
        Measure("first-receive-state-and-dictionary-storage", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].AcceptReceived(2, 0, 16);
        });
        Measure("consume-and-complete-receive-stream-retain-one-pooled-state", round, count, () =>
        {
            for (var i = 0; i < count; i++)
            {
                Require(controllers[i].RecordConsumed(2, 0, 16) == 0, "unexpected update threshold");
                Require(controllers[i].FlushConsumed(2, 0) == 16, "receive completion delta");
            }
        });
        Measure("complete-connection-clear-tables-and-pools", round, count, () =>
        {
            for (var i = 0; i < count; i++) controllers[i].Complete(Terminal);
        });
        GC.KeepAlive(controllers);
        GC.KeepAlive(waits);

        var warmed = NewController();
        for (var i = 0; i < 64; i++) ChurnSend(warmed, i);
        for (var i = 0; i < 64; i++) ChurnReceive(warmed, i);
        Measure("warmed-single-state-send-churn-reserve-return-complete", round, count, () =>
        {
            for (var i = 0; i < count; i++) ChurnSend(warmed, 1000L + i);
        });
        Measure("warmed-single-state-receive-churn-accept-consume-complete", round, count, () =>
        {
            for (var i = 0; i < count; i++) ChurnReceive(warmed, 1000L + i);
        });
        Require(warmed.SendConnectionCredit == Defaults.FlowControl.ConnectionReceiveWindowBytes &&
                warmed.ActiveSendStreamCount == 0 && warmed.RetainedSendStreamCount == 0,
                "warmed controller did not settle");
        warmed.Complete(Terminal);
        GC.KeepAlive(warmed);
    }

    private static StreamFlowController NewController() => new(
        Defaults.FlowControl.StreamReceiveWindowBytes,
        Defaults.FlowControl.ConnectionReceiveWindowBytes,
        Defaults.Protocol.MaxFramePayloadBytes,
        Defaults.Protocol.MaxConcurrentStreamsPerConnection);

    private static void ChurnSend(StreamFlowController controller, long key)
    {
        Require(controller.TryAcquireSendCredit(key, 0, 16), "warmed reserve");
        controller.ReturnUnsentCredit(key, 0, 16);
        controller.CompleteSendStream(key, 0);
    }
    private static void ChurnReceive(StreamFlowController controller, long key)
    {
        controller.AcceptReceived(key, 0, 16);
        Require(controller.RecordConsumed(key, 0, 16) == 0, "warmed receive threshold");
        Require(controller.FlushConsumed(key, 0) == 16, "warmed receive completion");
    }

    private static void Measure(string phase, int round, int count, Action body)
    {
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        body();
        var bytes = GC.GetAllocatedBytesForCurrentThread() - before;
        Require(thread == Environment.CurrentManagedThreadId, "measurement thread changed");
        GC.KeepAlive(body);
        if (round < 0) return;
        _output!.WriteLine(string.Join("\t", phase, round, count, bytes,
            ((double)bytes / count).ToString("R", CultureInfo.InvariantCulture)));
    }
    private static void Verify(string[] args)
    {
        Require(args.Length == 4, "Usage: <mode> <candidate-sha> <controller-sha256> <output.tsv>");
        Require(ModeIdentity.BaselineSha == Baseline, "compiled baseline mismatch");
        Require(args[0] is "baseline" or "split-gate" or "D-split-direction-gates", "requested mode");
        Require(ModeIdentity.Mode == args[0] && ModeIdentity.CandidateSha == args[1] &&
                ModeIdentity.ControllerSourceSha256 == args[2], "CLI cannot relabel compiled identity");
        ValidateSha(ModeIdentity.CandidateSha, 40);
        ValidateSha(ModeIdentity.RuntimeSourceTreeSha, 40);
        ValidateSha(ModeIdentity.MeasurementSourceSha, 64);
        var expected = ModeIdentity.Mode == "baseline" ? BaselineController : SplitController;
        Require(ModeIdentity.ControllerSourceSha256 == expected, "unreviewed controller digest");
        Require(Defaults.FlowControl.StreamReceiveWindowBytes == (1 << 20) &&
                Defaults.FlowControl.ConnectionReceiveWindowBytes == (16 << 20) &&
                Defaults.Protocol.MaxFramePayloadBytes == (4 << 20), "production defaults changed");
    }
    private static void ValidateSha(string value, int length)
    {
        Require(value.Length == length, "missing compiled source SHA");
        foreach (var c in value) Require(Uri.IsHexDigit(c), "invalid source SHA");
    }
    private static void Require(bool okay, string reason)
    {
        if (!okay) throw new InvalidOperationException(reason);
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static void Metadata(string key, string value) =>
        _output!.WriteLine("#" + key + "\t" + value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '));
}

