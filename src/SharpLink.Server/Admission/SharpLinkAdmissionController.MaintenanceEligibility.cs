namespace SharpLink.Server;

internal sealed partial class SharpLinkAdmissionController
{
    // Options validation makes all three queue bounds positive together. This is the exact
    // configuration gate used before AcquireAsync can enter WaitForAdmissionAsync, not a
    // snapshot of whether any requests happen to be queued at this moment.
    internal bool WaitingEnabled => _maxQueuedCalls != 0;
}
