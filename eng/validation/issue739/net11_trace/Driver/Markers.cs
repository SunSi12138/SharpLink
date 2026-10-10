using System.Diagnostics.Tracing;

namespace Issue739Net11;

[EventSource(Name = "SharpLink-Issue739")]
internal sealed class Markers : EventSource
{
    internal static readonly Markers Log = new();
    [Event(1)] public void Start(string sample) => WriteEvent(1, sample);
    [Event(2)] public void Stop(string sample, long bytes, long operations) => WriteEvent(2, sample, bytes, operations);
}
