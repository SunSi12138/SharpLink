namespace SharpLink.Server;

internal sealed partial class SharpLinkServer
{
    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        out ServerRequestPermit? permit)
        => _callAdmission.TryReserveCall(connection, out permit);

    internal ServerCallAdmissionResult TryReserveCall(
        ServerConnectionState connection,
        ServerRequestPermitTestHooks? testHooks,
        out ServerRequestPermit? permit)
        => _callAdmission.TryReserveCall(connection, testHooks, out permit);
}
