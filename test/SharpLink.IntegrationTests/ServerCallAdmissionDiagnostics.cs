namespace SharpLink.IntegrationTests;

internal static class ServerCallAdmissionDiagnostics
{
    internal static int ActiveCallCount(ISharpLinkServer server)
        => ((SharpLinkServer)server).ActiveCallCountForDiagnostics;

    internal static int PendingCallAdmissions(ISharpLinkServer server)
        => ((SharpLinkServer)server).PendingCallAdmissionsForDiagnostics;
}
