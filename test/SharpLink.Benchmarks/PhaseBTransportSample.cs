namespace SharpLink.Benchmarks;

// Shared data contract: the minimal native host does not link BenchmarkDotNet.
internal static partial class PhaseBTransportEvidenceRunner
{
    internal sealed record Sample(string Mode, string Transport, int Streams, int ItemsPerStream,
        int ItemBytes, int Round, int StreamWindow, int ConnectionWindow, int GrantBytes,
        long ItemsReceived, long BytesReturned, long UpdateFrames, long OwnerCommands,
        long RefillCommands, long PressureRevocations, long RevocationSweeps, long QueuedWaiterAdmissions,
        double ElapsedMs, double CpuMs, double ItemsPerSecond, double AllocatedBytesPerItem,
        double OwnerCommandsPerItem, double ProducerSpreadMs, double[] ProducerDurationMs,
        string Source, string ProtocolScope, System.Collections.Generic.Dictionary<string, long>? ReadyWriterMetrics = null);

}
