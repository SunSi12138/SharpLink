using System.Reflection;

namespace SharpLink.IntegrationTests;

internal static class ServerCallAdmissionDiagnostics
{
    private const BindingFlags InstanceFlags =
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static int ActiveCallCount(ISharpLinkServer server)
        => ReadIntProperty(GetCallAdmission(server), "ActiveCallCount");

    internal static int PendingCallAdmissions(ISharpLinkServer server)
        => ReadIntProperty(GetCallAdmission(server), "PendingCallAdmissions");

    private static object GetCallAdmission(ISharpLinkServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        return server.GetType().GetField("_callAdmission", InstanceFlags)?.GetValue(server) ??
            throw new InvalidOperationException("Server call-admission coordinator was not found.");
    }

    private static int ReadIntProperty(object value, string name)
        => (int)(value.GetType().GetProperty(name, InstanceFlags)?.GetValue(value) ??
            throw new InvalidOperationException($"Call-admission property '{name}' was not found."));
}
