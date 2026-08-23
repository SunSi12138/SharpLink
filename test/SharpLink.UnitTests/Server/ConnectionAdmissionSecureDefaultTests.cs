using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ConnectionAdmissionSecureDefaultTests
{
    [Test]
    public async Task DefaultHandshakeBoundIsIndependentAndNonZero()
    {
        var options = new SharpLinkConnectionAdmissionOptions();
        var clone = options.CloneValidated();

        await Assert.That(options.MaxConcurrentConnections)
            .IsEqualTo(SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentConnections);
        await Assert.That(options.MaxConcurrentHandshakes)
            .IsEqualTo(SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentHandshakes);
        await Assert.That(clone.MaxConcurrentHandshakes)
            .IsEqualTo(SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentHandshakes);
        await Assert.That(clone.MaxConcurrentHandshakes).IsLessThan(clone.MaxConcurrentConnections);
    }

    [Test]
    public async Task UnconfiguredHandshakeDefaultClampsToALowerConnectionBound()
    {
        var options = new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = 32
        };
        var clone = options.CloneValidated();

        await Assert.That(options.MaxConcurrentHandshakes).IsEqualTo(32);
        await Assert.That(clone.MaxConcurrentConnections).IsEqualTo(32);
        await Assert.That(clone.MaxConcurrentHandshakes).IsEqualTo(32);
    }

    [Test]
    public async Task ExplicitZeroKeepsTheDocumentedOptOutSemantics()
    {
        var options = new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = 256,
            MaxConcurrentHandshakes = 0
        };
        var clone = options.CloneValidated();
        var admission = new ServerConnectionAdmission(
            clone.MaxConcurrentConnections,
            clone.MaxConcurrentHandshakes);

        await Assert.That(clone.MaxConcurrentHandshakes).IsEqualTo(0);
        await Assert.That(admission.MaxHandshakes).IsEqualTo(256);
    }

    [Test]
    public async Task ExplicitHandshakeBoundAboveTheConnectionBoundStillFailsValidation()
    {
        var options = new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = 32,
            MaxConcurrentHandshakes = SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentHandshakes
        };

        var failure = await Assert.ThrowsAsync(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
        await Assert.That(failure).IsTypeOf<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task OneHundredThousandHandshakeLeaseCyclesReturnCountersToZero()
    {
        const int churn = 100_000;
        var admission = new ServerConnectionAdmission(maxConnections: 1, maxHandshakes: 1);

        for (var index = 0; index < churn; index++)
        {
            Ensure(admission.TryAcquireConnection(out var lease), "connection slot must be available");
            Ensure(admission.TryAcquireHandshake(lease), "handshake slot must be available");
            lease.ReleaseHandshake();
            lease.ReleaseConnection();
        }

        await Assert.That(admission.ActiveConnections).IsEqualTo(0);
        await Assert.That(admission.ActiveHandshakes).IsEqualTo(0);
    }

    [Test]
    public async Task ServerMaterializesAndLogsTheEffectiveSecureDefault()
    {
        var listener = new BlockingListener();
        using var provider = new AdmissionLogProvider();
        using var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(provider));
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .UseLoggerFactory(loggerFactory)
            .Build();

        await Assert.That(server.ConnectionAdmission.MaxConnections)
            .IsEqualTo(SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentConnections);
        await Assert.That(server.ConnectionAdmission.MaxHandshakes)
            .IsEqualTo(SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentHandshakes);

        using var runCts = new CancellationTokenSource();
        var runTask = server.RunAsync(runCts.Token).AsTask();
        try
        {
            var message = await provider.AdmissionConfigured.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(message).Contains(
                $"max_connections={SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentConnections}");
            await Assert.That(message).Contains(
                $"max_handshakes={SharpLinkConnectionAdmissionOptions.DefaultMaxConcurrentHandshakes}");
        }
        finally
        {
            await server.StopAsync(TimeSpan.Zero);
            try
            {
                await runTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private sealed class BlockingListener : IServerTransportListener
    {
        private readonly Channel<ITransportConnection> _connections =
            Channel.CreateUnbounded<ITransportConnection>();

        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(
            CancellationToken cancellationToken = default)
            => await _connections.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);

        public ValueTask DisposeAsync()
        {
            _connections.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class AdmissionLogProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<string> _messages = new();

        internal TaskCompletionSource<string> AdmissionConfigured { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

        public void Dispose()
        {
        }

        private sealed class CaptureLogger(AdmissionLogProvider owner) : ILogger
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
                var message = formatter(state, exception);
                owner._messages.Enqueue(message);
                if (eventId.Id == LogEvents.Server.ConnectionAdmissionConfigured)
                    owner.AdmissionConfigured.TrySetResult(message);
            }
        }
    }
}
