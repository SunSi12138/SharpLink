namespace SharpLink.Runtime;

internal sealed partial class PreAdmissionStreamDispatcher
{
    // Discard is monotonic for a stable route. Checking it under the route gate never grants
    // a stale business-call decision authority to abandon a newer live consumer.
    internal bool IsDiscarding
    {
        get
        {
            lock (_gate)
                return _discarding;
        }
    }
}
