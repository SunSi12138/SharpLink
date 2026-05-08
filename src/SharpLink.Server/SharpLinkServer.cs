namespace SharpLink.Server;

internal sealed partial class SharpLinkServer(
    ITransport transport,
    FrozenDictionary<long, (IRpcStub stub,object service)> services,
    TimeSpan heartbeatCheckInterval,
    TimeSpan heartbeatTimeout,
    ILoggerFactory loggerFactory,
    Func<string, SharpLinkAuthenticationResult>? authValidator = null) : IDisposable,ISharpLinkServer
{
    private readonly ConcurrentDictionary<string, IRpcSession> _sessions = [];
    private readonly ConcurrentDictionary<string, SharpLinkAuthenticationContext?> _sessionAuthContexts = [];
    private readonly ILogger _logger = (loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory))).CreateLogger<SharpLinkServer>();
    private readonly Func<string, SharpLinkAuthenticationResult> _authValidator = authValidator ?? DefaultAuthValidator;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly Lock _backgroundTasksGate = new();
    private readonly HashSet<Task> _backgroundTasks = [];
    private int _disposed;

    private static SharpLinkAuthenticationResult DefaultAuthValidator(string message)
        => !string.IsNullOrWhiteSpace(message)
            ? SharpLinkAuthenticationResult.Success
            : SharpLinkAuthenticationResult.Reject();
    

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdownCts.Cancel();
        DisposeAllSessions();
        transport.Dispose();
        WaitForBackgroundTasks();
        _shutdownCts.Dispose();
    }

    private void TrackBackgroundTask(Task task)
    {
        lock (_backgroundTasksGate)
            _backgroundTasks.Add(task);

        task.ContinueWith(
            static (completedTask, state) =>
            {
                var server = (SharpLinkServer)state!;
                lock (server._backgroundTasksGate)
                    server._backgroundTasks.Remove(completedTask);
            },
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeAllSessions()
    {
        foreach (var session in _sessions.Values)
            session.DisposeAsync().AsTask().GetAwaiter().GetResult();

        _sessionAuthContexts.Clear();
    }

    private void WaitForBackgroundTasks()
    {
        Task[] tasks;
        lock (_backgroundTasksGate)
            tasks = [.. _backgroundTasks];

        if (tasks.Length == 0)
            return;

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or System.IO.IOException or SocketException)
        {
        }
    }

    private SharpLinkCallContextSnapshot CreateCallContext(IRpcSession session)
    {
        _sessionAuthContexts.TryGetValue(session.Id, out var authenticationContext);
        return new SharpLinkCallContextSnapshot(session.Id, authenticationContext);
    }
}
