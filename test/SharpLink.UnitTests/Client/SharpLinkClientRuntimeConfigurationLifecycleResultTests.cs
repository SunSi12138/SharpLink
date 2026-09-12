using Microsoft.Extensions.Logging;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

[NotInParallel]
public sealed class SharpLinkClientRuntimeConfigurationLifecycleResultTests
{
    [Test]
    public async Task RunningLifecycleWithConnectionFaultShouldStillAcceptRuntimePublication()
    {
        var transport = new GatedFailingTransportFactory();
        using var loggerFactory = new BlockingSupervisorLoggerFactory();
        await using var client = ClientBuilderTestHelper.Build(
            transport,
            builder => builder.UseLoggerFactory(loggerFactory));

        await client.StartAsync();
        await transport.ConnectStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        transport.ReleaseFailure();
        await loggerFactory.SupervisorFailureLogged.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            Ensure(client.LifecycleState == SharpLinkClientLifecycleState.Running,
                "connection failure must not close the local runtime lifecycle");
            Ensure(client.State == SharpLinkConnectionState.Faulted,
                "test must observe the transient legacy connectivity Faulted window");
            Ensure(client.Readiness == SharpLinkReadinessState.NotReady,
                "connection failure should make readiness unavailable independently of lifecycle");

            var before = client.GetRequestTimeoutPolicySnapshot();
            var result = client.TryUpdateRequestTimeout(TimeSpan.FromSeconds(3));

            Ensure(result.Succeeded &&
                   result.FailureCode == SharpLinkRuntimeConfigurationUpdateFailureCode.None,
                "Running lifecycle must continue accepting runtime publication while connectivity is Faulted");
            var after = client.GetRequestTimeoutPolicySnapshot();
            Ensure(after.Generation == before.Generation + 1 &&
                   after.Enabled &&
                   after.Timeout == TimeSpan.FromSeconds(3),
                "accepted publication should advance exactly one complete request-timeout generation");
        }
        finally
        {
            loggerFactory.Release();
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception($"assert failed: {message}");
    }

    private sealed class GatedFailingTransportFactory : IClientTransportFactory
    {
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal TaskCompletionSource ConnectStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<ITransportConnection> ConnectAsync(
            CancellationToken cancellationToken = default)
        {
            ConnectStarted.TrySetResult();
            await _release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException("expected structured-result lifecycle connection failure");
        }

        internal void ReleaseFailure() => _release.TrySetResult();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class BlockingSupervisorLoggerFactory : ILoggerFactory
    {
        private readonly BlockingSupervisorLogger _logger = new();

        internal TaskCompletionSource SupervisorFailureLogged => _logger.SupervisorFailureLogged;

        public ILogger CreateLogger(string categoryName) => _logger;

        public void AddProvider(ILoggerProvider provider)
        {
        }

        internal void Release() => _logger.Release();

        public void Dispose() => Release();
    }

    private sealed class BlockingSupervisorLogger : ILogger
    {
        private readonly ManualResetEventSlim _release = new(initialState: false);

        internal TaskCompletionSource SupervisorFailureLogged { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (!message.Contains("RunInitialConnectivitySupervisorAsync", StringComparison.Ordinal))
                return;

            SupervisorFailureLogged.TrySetResult();
            _release.Wait(TimeSpan.FromSeconds(5));
        }

        internal void Release() => _release.Set();
    }
}
