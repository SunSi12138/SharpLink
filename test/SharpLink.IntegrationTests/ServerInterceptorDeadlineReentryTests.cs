using System.Net;
using System.Net.Sockets;

namespace SharpLink.IntegrationTests;

public class ServerInterceptorDeadlineReentryTests
{
    [Test]
    [NotInParallel]
    public async Task ExpiredCallShouldNotEnterLaterServerInterceptor()
    {
        var first = new PausingServerInterceptor();
        var second = new CountingServerInterceptor();
        using var serverCancellation = new CancellationTokenSource();
        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseTcp(0, IPAddress.Loopback.ToString())
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .AddInterceptor(first)
            .AddInterceptor(second);
        var port = ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        await using var server = serverBuilder.Build();
        var serverTask = Task.Run(
            () => server.RunAsync(serverCancellation.Token).AsTask(),
            CancellationToken.None);
        await using var client = SharpClientBuilder.Create()
            .UseTcp(IPAddress.Loopback.ToString(), port)
            .UseHeartbeat(TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2))
            .UseRequestTimeout(TimeSpan.FromMilliseconds(100))
            .Build();

        try
        {
            await client.ConnectAsync(serverCancellation.Token);
            var call = client.Get<IInterceptorTestService>()
                .DescribeNumberAsync(41)
                .AsTask();

            await first.Entered.WaitAsync(TimeSpan.FromSeconds(3));
            var failure = await CaptureSharpLinkException(call);
            Ensure(failure.Code == SharpLinkErrorCode.DeadlineExceeded,
                "the client call must terminate on its resolved deadline");

            // Release the first interceptor only after the logical call is terminal. Calling
            // next is a fresh user-code re-entry and must be rejected before the second
            // interceptor can execute any side effect.
            first.Release();
            await first.Finished.WaitAsync(TimeSpan.FromSeconds(3));
            Ensure(second.EntryCount == 0,
                "a later server interceptor must not run after the call deadline terminal wins");
        }
        finally
        {
            first.Release();
            await client.DisposeAsync();
            await serverCancellation.CancelAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    private static async Task<SharpLinkException> CaptureSharpLinkException(Task task)
    {
        try
        {
            await task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (SharpLinkException exception)
        {
            return exception;
        }
        throw new Exception("expected SharpLinkException");
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private sealed class PausingServerInterceptor : ISharpLinkServerInterceptor
    {
        private readonly TaskCompletionSource<bool> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _finished =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task Entered => _entered.Task;
        internal Task Finished => _finished.Task;

        internal void Release() => _release.TrySetResult(true);

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            _entered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            try
            {
                await next(context).ConfigureAwait(false);
            }
            finally
            {
                _finished.TrySetResult(true);
            }
        }
    }

    private sealed class CountingServerInterceptor : ISharpLinkServerInterceptor
    {
        private int _entryCount;

        internal int EntryCount => Volatile.Read(ref _entryCount);

        public async ValueTask InvokeAsync(
            SharpLinkServerInvocationContext context,
            SharpLinkServerInvocationDelegate next)
        {
            Interlocked.Increment(ref _entryCount);
            await next(context).ConfigureAwait(false);
        }
    }
}
