using System.Diagnostics.Tracing;

[EventSource(Name = "SharpLink-DiagnosticPhase")]
internal sealed class DiagnosticPhase : EventSource
{
    internal static readonly DiagnosticPhase Log = new();
    private DiagnosticPhase() { }

    public static class Keywords
    {
        public const EventKeywords Work = (EventKeywords)1;
    }

    [Event(1, Level = EventLevel.Informational, Opcode = EventOpcode.Info, Keywords = Keywords.Work)]
    public void WorkStart(string sourceRevision, string variant, string mode, int itemCount, int workSeconds, string window, long timestamp)
        => WriteEvent(1, sourceRevision, variant, mode, itemCount, workSeconds, window, timestamp);

    [Event(2, Level = EventLevel.Informational, Opcode = EventOpcode.Info, Keywords = Keywords.Work)]
    public void WorkStop(string sourceRevision, string variant, string mode, int itemCount, int workSeconds, string window, long timestamp)
        => WriteEvent(2, sourceRevision, variant, mode, itemCount, workSeconds, window, timestamp);
}
