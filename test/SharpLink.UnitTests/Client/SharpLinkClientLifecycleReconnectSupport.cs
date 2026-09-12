using System.Reflection;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SharpLink.Client;
using SharpLink.UnitTests.Runtime;
using static SharpLink.UnitTests.Client.SharpLinkClientLifecycleSharedSupport;

namespace SharpLink.UnitTests.Client;

internal static class SharpLinkClientLifecycleReconnectSupport
{
    internal static Task GetReadySignalTask(SharpLinkClient client)
        => ((TaskCompletionSource<bool>)(typeof(SharpLinkClient).GetField(
            "_readySignal",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("client has no active ready signal"))).Task;

    internal static ClientConnection GetClusterReadyConnection(
        SharpLinkClient client,
        string endpointId)
    {
        var clusterField = typeof(SharpLinkClient).GetField(
            "_cluster",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find endpoint cluster field");
        var cluster = clusterField.GetValue(client)
            ?? throw new Exception("client does not own an endpoint cluster");
        var statesField = cluster.GetType().GetField(
            cluster.GetType().Name.Contains("Dynamic", StringComparison.Ordinal)
                ? "_current"
                : "_endpoints",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new Exception("cannot find endpoint cluster state array");
        foreach (var state in (System.Collections.IEnumerable)statesField.GetValue(cluster)!)
        {
            var configuration = state.GetType().GetProperty("Configuration")!.GetValue(state)!;
            var endpoint = (SharpLinkEndpoint)configuration.GetType()
                .GetProperty("Endpoint")!
                .GetValue(configuration)!;
            if (!string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal))
                continue;
            var connections = (ClientConnection[])state.GetType()
                .GetProperty("ReadyConnections")!
                .GetValue(state)!;
            Ensure(connections.Length == 1,
                $"endpoint {endpointId} must own one deterministic ready connection");
            return connections[0];
        }
        throw new Exception($"cannot find ready endpoint {endpointId}");
    }

    internal static Task GetStaticReconnectTask(SharpLinkClient client, string endpointId)
    {
        var cluster = typeof(SharpLinkClient).GetField(
            "_cluster",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("client does not own an endpoint cluster");
        var states = (System.Collections.IEnumerable)(cluster.GetType().GetField(
            "_endpoints",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(cluster) ?? throw new Exception("cannot find static endpoint states"));
        foreach (var state in states)
        {
            var configuration = state.GetType().GetProperty("Configuration")!.GetValue(state)!;
            var endpoint = (SharpLinkEndpoint)configuration.GetType()
                .GetProperty("Endpoint")!
                .GetValue(configuration)!;
            if (string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal))
            {
                return (Task?)(state.GetType().GetProperty("ReconnectTask")!.GetValue(state))
                    ?? throw new Exception($"endpoint {endpointId} has no active reconnect owner");
            }
        }
        throw new Exception($"cannot find reconnect endpoint {endpointId}");
    }

    internal static Task GetDynamicReconnectTask(SharpLinkClient client, string endpointId)
    {
        var cluster = typeof(SharpLinkClient).GetField(
            "_cluster",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(client) ?? throw new Exception("client does not own an endpoint cluster");
        var states = (System.Collections.IEnumerable)(cluster.GetType().GetField(
            "_current",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(cluster) ?? throw new Exception("cannot find dynamic endpoint states"));
        foreach (var state in states)
        {
            var configuration = state.GetType().GetProperty("Configuration")!.GetValue(state)!;
            var endpoint = (SharpLinkEndpoint)configuration.GetType()
                .GetProperty("Endpoint")!
                .GetValue(configuration)!;
            if (string.Equals(endpoint.Id, endpointId, StringComparison.Ordinal))
            {
                return (Task?)(state.GetType().GetProperty("ReconnectTask")!.GetValue(state))
                    ?? throw new Exception($"endpoint {endpointId} has no active reconnect owner");
            }
        }
        throw new Exception($"cannot find reconnect endpoint {endpointId}");
    }

    internal static async Task ObserveConnectionFailureAsync(Task<int> operation)
    {
        try
        {
            _ = await operation;
            throw new Exception("expected the disconnected call to fail");
        }
        catch (SharpLinkException exception) when (exception.Code is
            SharpLinkErrorCode.ConnectionClosed or SharpLinkErrorCode.Unavailable)
        {
        }
    }

    internal sealed class TimerArmObservingTimeProvider(
        ManualTimeProvider inner,
        TimeSpan expectedDueTime) : TimeProvider
    {
        private readonly TaskCompletionSource _expectedTimerArmed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task ExpectedTimerArmed => _expectedTimerArmed.Task;

        public override long TimestampFrequency => inner.TimestampFrequency;

        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;

        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();

        public override long GetTimestamp() => inner.GetTimestamp();

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime == expectedDueTime)
                _expectedTimerArmed.TrySetResult();
            return timer;
        }
    }

    internal sealed class ChannelSnapshotResolver(SharpLinkEndpointSnapshot initial) : ISharpLinkEndpointResolver
    {
        private readonly Channel<SharpLinkEndpointSnapshot> _updates =
            Channel.CreateUnbounded<SharpLinkEndpointSnapshot>();
        private int _disposeCount;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public ValueTask<SharpLinkEndpointSnapshot> ResolveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(initial);
        }

        public async IAsyncEnumerable<SharpLinkEndpointSnapshot> WatchAsync(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var snapshot in _updates.Reader.ReadAllAsync(cancellationToken))
                yield return snapshot;
        }

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeCount, 1) == 0)
                _updates.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    internal sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly Lock _gate = new();

        internal List<LogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(CaptureLoggerFactory owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (owner._gate)
                    owner.Entries.Add(new LogEntry(logLevel, eventId, exception));
            }
        }
    }

    internal readonly record struct LogEntry(LogLevel Level, EventId EventId, Exception? Exception);
}
