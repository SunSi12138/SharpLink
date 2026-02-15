namespace SharpLink.Server;

internal sealed partial class SharpLinkServer(
    ITransport transport,
    ISerializer serializer,
    FrozenDictionary<long, (IRpcStub stub,object service)> services,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    ILoggerFactory loggerFactory) : IDisposable,ISharpLinkServer
{
    private readonly ConcurrentDictionary<string, IRpcSession> _sessions = [];
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SharpLinkServer>();

    //TODO:允许自定义验证
    private static bool AuthValidator(string s)
    {
        var res = !string.IsNullOrEmpty(s);
        return res;
    }
    

    public void Dispose()
    {
        transport.Dispose();
    }
    
}
