using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Channels;
using SharpLink.Server;

namespace SharpLink.UnitTests.Server;

public sealed class ConnectionAdmissionTests
{
    // ------------------------------------------------------------------ options

    [Test]
    public async Task ConnectionAdmissionOptionsRejectAHandshakeBoundAboveTheConnectionBound()
    {
        var options = new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = 8,
            MaxConcurrentHandshakes = 9
        };
        var failure = await Assert.ThrowsAsync(() =>
        {
            options.Validate();
            return Task.CompletedTask;
        });
        await Assert.That(failure).IsTypeOf<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task ConnectionAdmissionOptionsRejectNonPositiveBounds()
    {
        await Assert.ThrowsAsync(() =>
        {
            new SharpLinkConnectionAdmissionOptions { MaxConcurrentConnections = 0 }.Validate();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync(() =>
        {
            new SharpLinkConnectionAdmissionOptions { MaxConcurrentHandshakes = -1 }.Validate();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAsync(() =>
        {
            new SharpLinkConnectionAdmissionOptions { MaxConcurrentConnections = -1 }.Validate();
            return Task.CompletedTask;
        });
    }

    [Test]
    public async Task ZeroHandshakeBoundFollowsTheConnectionBound()
    {
        var options = new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = 8,
            MaxConcurrentHandshakes = 0
        };
        var clone = options.CloneValidated();
        var gate = new ServerConnectionAdmission(
            clone.MaxConcurrentConnections,
            clone.MaxConcurrentHandshakes);
        await Assert.That(gate.MaxHandshakes).IsEqualTo(8);
    }

    [Test]
    public async Task ConnectionAdmissionOptionsClonePreservesTheValidatedBounds()
    {
        var options = new SharpLinkConnectionAdmissionOptions
        {
            MaxConcurrentConnections = 42,
            MaxConcurrentHandshakes = 7
        };
        var clone = options.CloneValidated();
        await Assert.That(clone.MaxConcurrentConnections).IsEqualTo(42);
        await Assert.That(clone.MaxConcurrentHandshakes).IsEqualTo(7);
        options.MaxConcurrentConnections = 100;
        await Assert.That(clone.MaxConcurrentConnections).IsEqualTo(42);
    }

    // ------------------------------------------------------------------ gate

    [Test]
    public async Task ConnectionGateRejectsAcquisitionBeyondTheLimitAndReadmitsAfterRelease()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 2, maxHandshakes: 2);
        Ensure(gate.TryAcquireConnection(out var first), "first connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var second), "second connection must be admitted");
        Ensure(!gate.TryAcquireConnection(out _), "third connection must be rejected");
        await Assert.That(gate.ActiveConnections).IsEqualTo(2);

        first.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(1);
        Ensure(gate.TryAcquireConnection(out var third), "a released slot must admit a new connection");
        await Assert.That(gate.ActiveConnections).IsEqualTo(2);

        second.ReleaseConnection();
        third.ReleaseConnection();
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
    }

    [Test]
    public async Task HandshakeGateRejectsAcquisitionBeyondItsIndependentLimit()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 4, maxHandshakes: 2);
        Ensure(gate.TryAcquireConnection(out var first), "connection must be admitted");
        Ensure(gate.TryAcquireConnection(out var second), "connection must be admitted");

        Ensure(gate.TryAcquireHandshake(first), "first handshake must be admitted");
        Ensure(gate.TryAcquireHandshake(second), "second handshake must be admitted");
        Ensure(!gate.TryAcquireHandshake(first), "a handshake beyond the bound must be rejected");
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(2);

        first.ReleaseHandshake();
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(1);
        Ensure(gate.TryAcquireHandshake(first), "a released handshake slot must admit a new handshake");
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(2);
    }

    [Test]
    public async Task LeaseReleasesAreIdempotent()
    {
        var gate = new ServerConnectionAdmission(maxConnections: 2, maxHandshakes: 2);
        Ensure(gate.TryAcquireConnection(out var lease), "connection must be admitted");
        Ensure(gate.TryAcquireHandshake(lease), "handshake must be admitted");

        lease.ReleaseHandshake();
        lease.ReleaseHandshake();
        lease.ReleaseConnection();
        lease.ReleaseConnection();
        await Assert.That(gate.ActiveHandshakes).IsEqualTo(0);
        await Assert.That(gate.ActiveConnections).IsEqualTo(0);
    }

    // ------------------------------------------------------------------ server: connection bound

    [Test]
    public async Task SecondConnectionIsRejectedAndDisposedExactlyOnceWhileTheLimitIsHeld()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 1);

        var first = new TestConnection("first");
        listener.Enqueue(first);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 1,
            "the first connection must hold the only admission slot");

        var rejected = new TestConnection("rejected");
        listener.Enqueue(rejected);
        await YieldUntilAsync(
            () => rejected.DisposeCount == 1,
            "the rejected connection must be disposed");
        await YieldUntilAsync(
            () => rejected.AuthenticateCalls == 0,
            "the rejected connection must never enter the handshake lifecycle");
        await Assert.That(rejected.DisposeCount).IsEqualTo(1);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveConnections).IsEqualTo(1);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveHandshakes).IsEqualTo(1);

        // Terminal cleanup of the first connection returns the slot, which admits a new one.
        first.CompleteFeedInput();
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0,
            "the terminal cleanup must release the connection slot");

        var third = new TestConnection("third");
        listener.Enqueue(third);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 1,
            "a released slot must admit a new connection");
    }

    [Test]
    public async Task TlsHandshakeThrowReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4);

        var connection = new TestConnection(
            "tls-throw",
            static _ => throw new AuthenticationException("forced TLS failure"));
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => connection.DisposeCount == 1,
            "a failing TLS handshake must dispose the transport");
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0,
            "the TLS failure must release the connection slot");
        await Assert.That(harness.Server.ConnectionAdmission.ActiveHandshakes).IsEqualTo(0);
    }

    [Test]
    public async Task ProtocolHandshakeRejectReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4);

        var connection = new TestConnection("protocol-reject");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the connection must hold a handshake slot while the protocol handshake runs");

        // A Ping is not allowed before the handshake completes: the server rejects.
        WritePingFrame(connection.FeedInput);
        await YieldUntilAsync(
            () => connection.DisposeCount == 1,
            "the protocol rejection must dispose the transport");
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the protocol rejection must release both slots");
    }

    [Test]
    public async Task AuthenticatorRejectReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(
            listener,
            options => options.MaxConcurrentConnections = 4,
            authenticator: new RejectingAuthenticator());

        var connection = new TestConnection("auth-reject");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the connection must hold a handshake slot during authentication");
        WriteValidHandshakeRequest(connection.FeedInput, harness.Limits);
        await YieldUntilAsync(
            () => connection.DisposeCount == 1,
            "the authentication rejection must dispose the transport");
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the authentication rejection must release both slots");
    }

    [Test]
    public async Task ReadyConnectionReleasesTheHandshakeSlotButKeepsTheConnectionSlot()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4);

        var connection = new TestConnection("ready");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the connection must hold a handshake slot before Ready");
        WriteValidHandshakeRequest(connection.FeedInput, harness.Limits);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 0 &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 1,
            "Ready must release the handshake slot while the connection slot stays held");

        connection.CompleteFeedInput();
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0,
            "the Ready disconnect must release the connection slot");
    }

    [Test]
    public async Task HandshakeLimitRejectsAdditionalConnectionsImmediately()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
        {
            options.MaxConcurrentConnections = 4;
            options.MaxConcurrentHandshakes = 1;
        });

        var first = new TestConnection("handshaking");
        listener.Enqueue(first);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the first connection must hold the only handshake slot");

        var rejected = new TestConnection("handshake-rejected");
        listener.Enqueue(rejected);
        await YieldUntilAsync(
            () => rejected.DisposeCount == 1,
            "a connection over the handshake bound must be closed immediately");
        await Assert.That(rejected.AuthenticateCalls).IsEqualTo(0);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveConnections).IsEqualTo(1);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveHandshakes).IsEqualTo(1);
    }

    [Test]
    public async Task StopWhileAHandshakeIsStalledReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 8);

        for (var index = 0; index < 8; index++)
        {
            listener.Enqueue(new TestConnection(
                $"stalled-{index}",
                async (CancellationToken cancellationToken) =>
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)));
        }
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 8 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 8,
            "all stalled connections must hold both slots");

        await harness.StopAsync();
        await Assert.That(harness.Server.ConnectionAdmission.ActiveConnections).IsEqualTo(0);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveHandshakes).IsEqualTo(0);
    }

    [Test]
    public async Task ProtocolHandshakeTimeoutReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(
            listener,
            options => options.MaxConcurrentConnections = 4,
            protocol: protocolOptions => protocolOptions.HandshakeTimeout = TimeSpan.FromMilliseconds(200));

        var connection = new TestConnection("timeout");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the stalled connection must hold a handshake slot");
        await YieldUntilAsync(
            () => connection.DisposeCount == 1 &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 0,
            "the handshake timeout must dispose the transport and release both slots",
            fastPollAttempts: 4000);
    }

    [Test]
    public async Task ConnectionChurnReturnsTheCounterToZero()
    {
        const int churn = 2000;
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4096);

        var disposed = 0;
        for (var index = 0; index < churn; index++)
        {
            var connection = new TestConnection($"churn-{index}");
            connection.CompleteFeedInput();
            connection.Disposed += () => Interlocked.Increment(ref disposed);
            listener.Enqueue(connection);
        }

        await YieldUntilAsync(
            () => Volatile.Read(ref disposed) == churn &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 0,
            "every churned connection must reach terminal cleanup with the counter at zero",
            fastPollAttempts: 20000);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveHandshakes).IsEqualTo(0);
    }

    [Test]
    public async Task DuplicateConnectionIdDoesNotDoubleReleaseTheSlot()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4);

        var first = new TestConnection("duplicate");
        var second = new TestConnection("duplicate");
        first.CompleteFeedInput();
        second.CompleteFeedInput();
        listener.Enqueue(first);
        listener.Enqueue(second);

        await YieldUntilAsync(
            () => first.DisposeCount == 1 && second.DisposeCount == 1,
            "both duplicate-id connections must reach terminal cleanup");
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the duplicate/replace path must not double-release the slot");
    }

    [Test]
    public async Task RejectionTelemetryRecordsReasonedRejections()
    {
        using var listener = new MeterListener();
        var rejected = 0L;
        var reasons = new List<string>();
        listener.InstrumentPublished = static (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == "SharpLink" &&
                instrument.Name == "sharplink.connections.rejected")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!instrument.Name.Equals("sharplink.connections.rejected", StringComparison.Ordinal))
                return;
            Interlocked.Add(ref rejected, value);
            foreach (var tag in tags)
            {
                if (tag.Key == "sharplink.admission.reason" && tag.Value is string reason)
                {
                    lock (reasons)
                        reasons.Add(reason);
                }
            }
        });
        listener.Start();

        var scripted = new ScriptedListener();
        await using var harness = await StartServerAsync(scripted, options =>
            options.MaxConcurrentConnections = 1);

        scripted.Enqueue(new TestConnection("telemetry-holder"));
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 1,
            "the holder must occupy the only slot");
        for (var index = 0; index < 3; index++)
            scripted.Enqueue(new TestConnection($"telemetry-rejected-{index}"));
        await YieldUntilAsync(
            () => Volatile.Read(ref rejected) >= 3,
            "every rejected connection must be recorded by telemetry");
        await Assert.That(reasons.Contains("connection_limit")).IsTrue();
    }

    // ------------------------------------------------------------------ server: real TCP

    [Test]
    public async Task TcpLimitRejectsTheSecondConnectionUntilTheFirstTerminates()
    {
        var listener = new SocketServerTransportListener(new IPEndPoint(IPAddress.Loopback, 0));
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .UseConnectionAdmission(options => options.MaxConcurrentConnections = 1)
            .Build();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var runCts = new CancellationTokenSource();
        var runTask = server.RunAsync(runCts.Token).AsTask();
        try
        {
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await YieldUntilAsync(
                () => server.ConnectionAdmission.ActiveConnections == 1,
                "the first TCP connection must hold the only slot");

            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            var secondClosed = await ReadUntilClosedAsync(second);
            Ensure(secondClosed, "the second TCP connection must be closed immediately by admission");
            await Assert.That(server.ConnectionAdmission.ActiveConnections).IsEqualTo(1);

            first.Close();
            await YieldUntilAsync(
                () => server.ConnectionAdmission.ActiveConnections == 0,
                "closing the first connection must release the slot");

            using var third = new TcpClient();
            await third.ConnectAsync(IPAddress.Loopback, port);
            await YieldUntilAsync(
                () => server.ConnectionAdmission.ActiveConnections == 1,
                "the third TCP connection must be admitted after release");
            var thirdClosed = await ReadUntilClosedAsync(third, graceMs: 1000);
            Ensure(!thirdClosed, "the admitted third connection must remain open");
        }
        finally
        {
            await StopServerAsync(server, runCts, runTask);
        }
    }

    [Test]
    public async Task TlsHandshakeLimitRejectsTheSecondConnectionWhileTheFirstStalls()
    {
        var certificate = CreateCertificate();
        var listener = new SocketServerTransportListener(
            new IPEndPoint(IPAddress.Loopback, 0),
            backlog: 64,
            tlsOptions: new SslServerAuthenticationOptions { ServerCertificate = certificate });
        await using var server = (SharpLinkServer)SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .UseConnectionAdmission(options =>
            {
                options.MaxConcurrentConnections = 2;
                options.MaxConcurrentHandshakes = 1;
            })
            .Build();
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        using var runCts = new CancellationTokenSource();
        var runTask = server.RunAsync(runCts.Token).AsTask();
        try
        {
            // Never send the ClientHello: the server parks in the TLS handshake and holds
            // the only handshake slot.
            using var first = new TcpClient();
            await first.ConnectAsync(IPAddress.Loopback, port);
            await YieldUntilAsync(
                () => server.ConnectionAdmission.ActiveHandshakes == 1,
                "the TLS-stalled connection must hold the only handshake slot");

            using var second = new TcpClient();
            await second.ConnectAsync(IPAddress.Loopback, port);
            var secondClosed = await ReadUntilClosedAsync(second);
            Ensure(secondClosed, "a connection over the TLS handshake bound must be closed immediately");
            // Observing the close from the client side is not a sync point for the server
            // accounting: the connection lease is released only after terminal cleanup
            // completes, so ActiveConnections may transiently still be 2 here.
            await YieldUntilAsync(
                () => server.ConnectionAdmission.ActiveConnections == 1,
                "rejected TLS connection must release its connection slot after terminal cleanup");
            await Assert.That(server.ConnectionAdmission.ActiveHandshakes).IsEqualTo(1);
        }
        finally
        {
            await StopServerAsync(server, runCts, runTask);
        }
    }

    [Test]
    public async Task ConnectionSlotStaysHeldUntilTerminalCleanupCompletes()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 1);
        var disposeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var connection = new TestConnection("slow-dispose", disposeGate: disposeGate);
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 1,
            "the first connection must hold the only admission slot");
        WritePingFrame(connection.FeedInput);
        await YieldUntilAsync(
            () => connection.DisposeCount == 1,
            "the protocol rejection must start terminal cleanup");

        // The slot must not be released while the terminal disposal is still in flight.
        await Task.Delay(50);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveConnections).IsEqualTo(1);

        disposeGate.TrySetResult();
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "terminal cleanup completion must release the slots");
    }

    [Test]
    public async Task DisposeFailureStillReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4);

        var connection = new TestConnection(
            "dispose-throw",
            disposeException: new InvalidOperationException("forced dispose failure"));
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the connection must hold a handshake slot before termination");
        connection.CompleteFeedInput();
        await YieldUntilAsync(
            () => connection.DisposeCount == 1,
            "the failing disposal must still run");
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "a disposal failure must not leak the admission slots");
    }

    [Test]
    public async Task RejectedConnectionDisposeFailureDoesNotFaultTheServer()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 1);

        var holder = new TestConnection("holder");
        listener.Enqueue(holder);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 1,
            "the holder must occupy the only slot");

        var rejected = new TestConnection(
            "rejected-dispose-throw",
            disposeException: new InvalidOperationException("forced dispose failure"));
        listener.Enqueue(rejected);
        await YieldUntilAsync(
            () => rejected.DisposeCount == 1,
            "the rejected connection must still be disposed");

        await Assert.That(harness.Server.HealthStatus).IsEqualTo(SharpLinkHealthStatus.Ready);
        await Assert.That(harness.Server.ConnectionAdmission.ActiveConnections).IsEqualTo(1);

        // The accept loop keeps working after the failed rejection cleanup.
        var next = new TestConnection("next-rejected");
        listener.Enqueue(next);
        await YieldUntilAsync(
            () => next.DisposeCount == 1,
            "subsequent connections must still be rejected while the slot is held");
    }

    [Test]
    public async Task ProtocolRejectSendsAnErrorResponseAndReleasesBothSlots()
    {
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, options =>
            options.MaxConcurrentConnections = 4);

        var connection = new TestConnection("protocol-reject-response");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "handshake slot held");

        WritePingFrame(connection.FeedInput);
        var readTask = connection.ObserveOutput.ReadAsync().AsTask();
        var winner = await Task.WhenAny(readTask, Task.Delay(3000));
        Ensure(winner == readTask, "the server must answer the protocol violation with an error frame");
        var result = await readTask;
        var bytes = result.Buffer.Length;
        connection.ObserveOutput.AdvanceTo(result.Buffer.End);
        Ensure(bytes > 0, "the server must answer the protocol violation with a non-empty error frame");
        await YieldUntilAsync(
            () => connection.DisposeCount == 1 &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the protocol rejection must dispose the transport and release both slots");
    }

    // ------------------------------------------------------------------ helpers

    private sealed class ScriptedListener : IServerTransportListener
    {
        private readonly Channel<ITransportConnection> _channel =
            Channel.CreateUnbounded<ITransportConnection>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });

        public EndPoint? LocalEndPoint => null;

        public async ValueTask<ITransportConnection> AcceptAsync(CancellationToken cancellationToken = default)
        {
            var connection = await _channel.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }

        public ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        internal void Enqueue(ITransportConnection connection)
        {
            if (!_channel.Writer.TryWrite(connection))
                throw new InvalidOperationException("The scripted listener was already disposed.");
        }
    }

    private sealed class TestConnection : ITransportConnection, ITransportSecurityHandshake
    {
        private readonly Pipe _inputPipe = new();
        private readonly Pipe _outputPipe = new();
        private readonly Func<CancellationToken, ValueTask> _authenticateAsync;
        private readonly TaskCompletionSource? _disposeGate;
        private readonly Exception? _disposeException;
        private int _disposeCount;
        private int _authenticateCalls;

        internal TestConnection(
            string id,
            Func<CancellationToken, ValueTask>? authenticateAsync = null,
            TaskCompletionSource? disposeGate = null,
            Exception? disposeException = null)
        {
            Id = id;
            _authenticateAsync = authenticateAsync ??
                ((Func<CancellationToken, ValueTask>)(static _ => ValueTask.CompletedTask));
            _disposeGate = disposeGate;
            _disposeException = disposeException;
        }

        public string Id { get; }

        public PipeReader Input => _inputPipe.Reader;

        public PipeWriter Output => _outputPipe.Writer;

        public EndPoint? LocalEndPoint => null;

        public EndPoint? RemoteEndPoint => null;

        internal PipeWriter FeedInput => _inputPipe.Writer;

        internal PipeReader ObserveOutput => _outputPipe.Reader;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        internal int AuthenticateCalls => Volatile.Read(ref _authenticateCalls);

        internal event Action? Disposed;

        public ValueTask AuthenticateAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _authenticateCalls);
            return _authenticateAsync(cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            Disposed?.Invoke();
            if (_disposeGate is not null)
                await _disposeGate.Task.ConfigureAwait(false);
            if (_disposeException is not null)
                throw _disposeException;
        }

        internal void CompleteFeedInput()
        {
            try
            {
                _inputPipe.Writer.Complete();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class RejectingAuthenticator : ISharpLinkServerAuthenticator
    {
        public ValueTask<SharpLinkAuthenticationResult> AuthenticateAsync(
            SharpLinkAuthenticationRequest request,
            CancellationToken cancellationToken)
            => ValueTask.FromResult(SharpLinkAuthenticationResult.Reject(
                SharpLinkErrorCode.AuthenticationRejected,
                "forced rejection"));
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _runCts = new();
        private bool _disposed;

        internal ServerHarness(SharpLinkServer server, Task runTask, SharpLinkProtocolOptions limits)
        {
            Server = server;
            RunTask = runTask;
            Limits = limits;
        }

        internal SharpLinkServer Server { get; }

        internal Task RunTask { get; }

        internal SharpLinkProtocolOptions Limits { get; }

        internal async Task StopAsync()
        {
            _disposed = true;
            await StopServerAsync(Server, _runCts, RunTask);
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopAsync();
        }
    }

    private static async Task<ServerHarness> StartServerAsync(
        ScriptedListener listener,
        Action<SharpLinkConnectionAdmissionOptions> configureAdmission,
        ISharpLinkServerAuthenticator? authenticator = null,
        Action<SharpLinkProtocolOptions>? protocol = null)
    {
        var limits = new SharpLinkProtocolOptions();
        protocol?.Invoke(limits);
        var builder = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .UseProtocol(options => options.HandshakeTimeout = limits.HandshakeTimeout)
            .UseConnectionAdmission(configureAdmission);
        if (authenticator is not null)
            builder.UseAuthenticator(authenticator);
        var server = (SharpLinkServer)builder.Build();
        var runCts = new CancellationTokenSource();
        var runTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(runCts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, runCts.Token);
        // Wait until the accept loop is actually running before enqueueing connections.
        await YieldUntilAsync(
            () => server.HealthStatus == SharpLinkHealthStatus.Ready,
            "the scripted server must reach Running");
        return new ServerHarness(server, runTask, limits);
    }

    private static async Task StopServerAsync(
        SharpLinkServer server,
        CancellationTokenSource runCts,
        Task runTask)
    {
        try
        {
            await server.StopAsync(TimeSpan.Zero);
        }
        catch
        {
        }
        runCts.Cancel();
        try
        {
            await runTask;
        }
        catch
        {
        }
        runCts.Dispose();
        try
        {
            await server.DisposeAsync();
        }
        catch
        {
        }
    }

    private static async Task<bool> ReadUntilClosedAsync(TcpClient client, int graceMs = 3000)
    {
        var read = client.GetStream().ReadAsync(new byte[1]).AsTask();
        var completed = await Task.WhenAny(read, Task.Delay(graceMs));
        if (completed != read)
            return false;
        try
        {
            return (await read) == 0;
        }
        catch (Exception exception) when (
            exception is IOException or SocketException or ObjectDisposedException)
        {
            return true;
        }
    }

    private static void WritePingFrame(PipeWriter output)
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer, ProtocolV2FrameType.Ping, ProtocolV2FrameFlags.None, 0);
        var span = writer.GetSpan(sizeof(long));
        System.Buffers.Binary.BinaryPrimitives.WriteInt64LittleEndian(span, 42);
        writer.Advance(sizeof(long));
        ProtocolV2FrameWriter.EndFrame(writer, token);
        output.Write(writer.WrittenMemory.ToArray());
        output.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    private static void WriteValidHandshakeRequest(PipeWriter output, SharpLinkProtocolOptions limits)
    {
        var writer = new PooledByteBufferWriter();
        var token = ProtocolV2FrameWriter.BeginFrame(
            writer, ProtocolV2FrameType.HandshakeRequest, ProtocolV2FrameFlags.None, 0);
        var request = new ProtocolV2HandshakeRequest(
            ProtocolV2Constants.MinorVersion,
            ProtocolV2Capabilities.None,
            ProtocolV2Capabilities.None,
            limits.MaxFramePayloadBytes,
            1024 * 1024,
            16 * 1024 * 1024,
            ReadOnlyMemory<byte>.Empty,
            ReadOnlyMemory<string>.Empty);
        ProtocolV2PayloadCodec.WriteHandshakeRequest(writer, request, limits);
        ProtocolV2FrameWriter.EndFrame(writer, token);
        output.Write(writer.WrittenMemory.ToArray());
        output.FlushAsync().AsTask().GetAwaiter().GetResult();
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=sharplink-admission-tests",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            true));
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName("localhost");
        request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddMinutes(-5),
            DateTimeOffset.UtcNow.AddDays(2));
        return X509CertificateLoader.LoadPkcs12(
            generated.Export(X509ContentType.Pkcs12),
            password: null,
            X509KeyStorageFlags.DefaultKeySet);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static async Task YieldUntilAsync(Func<bool> condition, string failureMessage, int fastPollAttempts = 2000)
    {
        var deadline = Environment.TickCount64 + 15000;
        for (var attempt = 0; attempt < fastPollAttempts && !condition(); attempt++)
        {
            if (Environment.TickCount64 >= deadline)
                break;
            if (attempt % 32 == 0)
                await Task.Delay(1);
            else
                await Task.Yield();
        }
        // The fast-polling budget can be exhausted long before the deadline on fast
        // machines (4000 yields take ~190ms on a 32-core host, outrunning the 200ms
        // handshake-timeout timer that ProtocolHandshakeTimeoutReleasesBothSlots waits
        // for). Fall back to coarse polling so the deadline is the sole wait boundary
        // for timer-driven conditions.
        while (!condition() && Environment.TickCount64 < deadline)
            await Task.Delay(5);
        Ensure(condition(), failureMessage);
    }
}
