namespace SharpLink.LoadTestBase;

public enum RunMode
{
    Local,
    Server,
    Client
}

public enum TransportMode
{
    Tcp,
    Uds,
    NamedPipe,
    AnonymousPipe
}
