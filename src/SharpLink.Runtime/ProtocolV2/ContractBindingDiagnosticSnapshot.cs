namespace SharpLink.Runtime;

internal readonly record struct ContractBindingDiagnosticSnapshot(
    int Bindings, int Operations, int ProtectedRequests, int DemandSites, int PrepareWaiters, int ReceivedUnpublishedAcks);
