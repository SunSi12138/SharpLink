using System.Reflection;

namespace SharpLink.IntegrationTests;

internal static class ServerCallAdmissionDiagnostics
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.NonPublic;

    internal static int ActiveCallCount(ISharpLinkServer server)
        => GetCallAdmission(server).ActiveCallCount;

    internal static int PendingCallAdmissions(ISharpLinkServer server)
        => GetCallAdmission(server).PendingCallAdmissions;

    private static ServerCallAdmission GetCallAdmission(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.GetType().GetField("_callAdmission", InstanceFlags)?.GetValue(server) as ServerCallAdmission ??
            throw new InvalidOperationException("Server call-admission coordinator was not found.");
    }
}
