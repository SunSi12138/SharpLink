using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using SharpLink.Server;
using SharpLink.UnitTests.Runtime;

namespace SharpLink.UnitTests.Server;

/// <summary>
/// Security regression tests: hostile Protocol v2 input and extension-authenticator
/// failures must produce bounded, payload-free, exception-free logs while telemetry
/// keeps counting every real event. The class is NotInParallel because the MeterListener
/// used below observes process-wide instruments.
/// </summary>
[NotInParallel]
public class SharpLinkServerSecurityLoggingTests
{
    [Test]
    public async Task InvalidMagicDuringHandshakeEmitsDedicatedWarningWithoutEchoingPayloadOrException()
    {
        var timeProvider = new ManualTimeProvider();
        var loggerFactory = new CaptureLoggerFactory();
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, loggerFactory, timeProvider);

        var connection = new TestConnection("hostile-magic");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the hostile connection must hold the handshake slot");

        WriteInvalidMagicFrame(connection.FeedInput, Encoding.ASCII.GetBytes("DE AD BE EF SECRET TOKEN"));

        await YieldUntilAsync(
            () => connection.DisposeCount >= 1 &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the hostile connection must be closed and every admission slot released");

        var violations = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Connection.ProtocolViolation)
            .ToList();
        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(violations[0].Exception).IsNull();
        await Assert.That(violations[0].Message.Contains("invalid_magic", StringComparison.Ordinal)).IsTrue();
        await Assert.That(violations[0].Message.Contains("prefix=", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(violations[0].Message.Contains("DEADBEEF", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(violations[0].Message.Contains("SECRET", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(violations[0].Message.Contains("TOKEN", StringComparison.OrdinalIgnoreCase)).IsFalse();
        await Assert.That(violations[0].Message.Contains("at SharpLink", StringComparison.Ordinal)).IsFalse();

        var backgroundErrors = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Server.BackgroundLoopUnhandledException)
            .ToList();
        await Assert.That(backgroundErrors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ProtocolViolationStormIsRateLimitedWhileEveryEventIsTelemetryCounted()
    {
        const int stormSize = 100;
        var timeProvider = new ManualTimeProvider();
        var loggerFactory = new CaptureLoggerFactory();
        var listener = new ScriptedListener();
        long serverFailures = 0;

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, _) =>
        {
            if (instrument.Name == "sharplink.protocol.failures")
                meterListener.EnableMeasurementEvents(instrument);
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
        {
            if (!instrument.Name.Equals("sharplink.protocol.failures", StringComparison.Ordinal))
                return;
            foreach (var tag in tags)
            {
                if (tag.Key == "rpc.side" && tag.Value is "server")
                    Interlocked.Add(ref serverFailures, value);
            }
        });
        meterListener.Start();

        await using var harness = await StartServerAsync(listener, loggerFactory, timeProvider);
        for (var index = 0; index < stormSize; index++)
        {
            var connection = new TestConnection($"storm-{index}");
            listener.Enqueue(connection);
            WriteInvalidMagicFrame(connection.FeedInput, Encoding.ASCII.GetBytes($"storm-payload-{index}"));
            await YieldUntilAsync(
                () => connection.DisposeCount >= 1,
                $"storm connection {index} must be closed");
        }
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the storm must release every admission slot");

        // Telemetry counts every real event; the warning log is bounded to one per window.
        await Assert.That(Volatile.Read(ref serverFailures)).IsEqualTo(stormSize);
        await Assert.That(CountEntries(loggerFactory, LogEvents.Connection.ProtocolViolation)).IsEqualTo(1);
        await Assert.That(CountEntries(loggerFactory, LogEvents.Connection.ProtocolViolationSuppressed)).IsEqualTo(0);

        // The next throttle window admits again and reports the suppressed count.
        timeProvider.Advance(TimeSpan.FromSeconds(5));
        var boundary = new TestConnection("window-boundary");
        listener.Enqueue(boundary);
        WriteInvalidMagicFrame(boundary.FeedInput, Encoding.ASCII.GetBytes("boundary"));
        await YieldUntilAsync(
            () => boundary.DisposeCount >= 1,
            "the boundary connection must be closed");

        await Assert.That(Volatile.Read(ref serverFailures)).IsEqualTo(stormSize + 1);
        await Assert.That(CountEntries(loggerFactory, LogEvents.Connection.ProtocolViolation)).IsEqualTo(2);
        var suppressed = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Connection.ProtocolViolationSuppressed)
            .ToList();
        await Assert.That(suppressed.Count).IsEqualTo(1);
        await Assert.That(suppressed[0].Message).Contains("99");
    }

    [Test]
    public async Task AuthenticationProviderExceptionIsLoggedAsTypeAndFailureIdWithoutSecrets()
    {
        var timeProvider = new ManualTimeProvider();
        var loggerFactory = new CaptureLoggerFactory();
        var listener = new ScriptedListener();
        var authenticator = SharpLinkAuthenticator.CreateServer(
            static (_, _) => throw new InvalidOperationException(
                "secret-token=abc123 Authorization=Bearer very-sensitive-value"));
        await using var harness = await StartServerAsync(
            listener, loggerFactory, timeProvider, authenticator);

        await DriveHandshakeToAuthenticationAsync(listener, harness, "auth-secret-one");
        await DriveHandshakeToAuthenticationAsync(listener, harness, "auth-secret-two");

        var authWarnings = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Connection.AuthenticationProviderFailed)
            .ToList();
        await Assert.That(authWarnings.Count).IsEqualTo(2);
        foreach (var warning in authWarnings)
        {
            await Assert.That(warning.Level).IsEqualTo(LogLevel.Warning);
            await Assert.That(warning.Exception).IsNull();
            await Assert.That(warning.Message.Contains("ExceptionType=System.InvalidOperationException", StringComparison.Ordinal)).IsTrue();
            await Assert.That(warning.Message.Contains("abc123", StringComparison.Ordinal)).IsFalse();
            await Assert.That(warning.Message.Contains("Authorization", StringComparison.Ordinal)).IsFalse();
            await Assert.That(warning.Message.Contains("Bearer", StringComparison.Ordinal)).IsFalse();
            await Assert.That(warning.Message.Contains("very-sensitive-value", StringComparison.Ordinal)).IsFalse();
            await Assert.That(warning.Message.Contains("secret-token", StringComparison.Ordinal)).IsFalse();
            await Assert.That(warning.Message.Contains("at SharpLink", StringComparison.Ordinal)).IsFalse();
        }
        await Assert.That(authWarnings[0].Message).Contains("FailureId=1");
        await Assert.That(authWarnings[1].Message).Contains("FailureId=2");

        var errors = loggerFactory.Entries
            .Where(entry => entry.Level == LogLevel.Error)
            .ToList();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task NestedAuthenticationProviderExceptionNeverLeaksOuterOrInnerMessages()
    {
        var timeProvider = new ManualTimeProvider();
        var loggerFactory = new CaptureLoggerFactory();
        var listener = new ScriptedListener();
        var authenticator = SharpLinkAuthenticator.CreateServer(
            static (_, _) => throw new AuthenticationException(
                "outer secret",
                new InvalidOperationException("inner-secret")));
        await using var harness = await StartServerAsync(
            listener, loggerFactory, timeProvider, authenticator);

        await DriveHandshakeToAuthenticationAsync(listener, harness, "auth-nested");

        var authWarnings = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Connection.AuthenticationProviderFailed)
            .ToList();
        await Assert.That(authWarnings.Count).IsEqualTo(1);
        await Assert.That(authWarnings[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(authWarnings[0].Exception).IsNull();
        await Assert.That(authWarnings[0].Message.Contains("FailureId=1", StringComparison.Ordinal)).IsTrue();
        await Assert.That(authWarnings[0].Message.Contains("ExceptionType=System.Security.Authentication.AuthenticationException", StringComparison.Ordinal)).IsTrue();
        await Assert.That(authWarnings[0].Message.Contains("outer secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(authWarnings[0].Message.Contains("inner-secret", StringComparison.Ordinal)).IsFalse();
        await Assert.That(authWarnings[0].Message.Contains("at SharpLink", StringComparison.Ordinal)).IsFalse();

        var errors = loggerFactory.Entries
            .Where(entry => entry.Level == LogLevel.Error)
            .ToList();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ValidlyEncodedForeignFrameDuringHandshakeEmitsClassifiedWarningWithoutGenericHandshakeLog()
    {
        var timeProvider = new ManualTimeProvider();
        var loggerFactory = new CaptureLoggerFactory();
        var listener = new ScriptedListener();
        await using var harness = await StartServerAsync(listener, loggerFactory, timeProvider);

        var connection = new TestConnection("wrong-first-frame");
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the foreign-frame connection must hold the handshake slot");

        // A validly encoded Ping is a protocol violation as the first handshake frame.
        // It is rejected (not thrown): the rejection must still reach the bounded,
        // classified Warning and must not fall back to the generic handshake Warning.
        WritePingFrame(connection.FeedInput);

        await YieldUntilAsync(
            () => connection.DisposeCount >= 1 &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the foreign-frame connection must be closed and release both slots");

        var violations = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Connection.ProtocolViolation)
            .ToList();
        await Assert.That(violations.Count).IsEqualTo(1);
        await Assert.That(violations[0].Level).IsEqualTo(LogLevel.Warning);
        await Assert.That(violations[0].Exception).IsNull();
        await Assert.That(violations[0].Message.Contains("protocol_state", StringComparison.Ordinal)).IsTrue();

        var handshakeFailures = loggerFactory.Entries
            .Where(entry => entry.EventId.Id == LogEvents.Connection.HandshakeFailed)
            .ToList();
        await Assert.That(handshakeFailures.Count).IsEqualTo(0);

        var errors = loggerFactory.Entries
            .Where(entry => entry.Level == LogLevel.Error)
            .ToList();
        await Assert.That(errors.Count).IsEqualTo(0);
    }

    // ------------------------------------------------------------------ helpers

    private static async Task DriveHandshakeToAuthenticationAsync(
        ScriptedListener listener,
        ServerHarness harness,
        string connectionId)
    {
        var connection = new TestConnection(connectionId);
        listener.Enqueue(connection);
        await YieldUntilAsync(
            () => harness.Server.ConnectionAdmission.ActiveHandshakes == 1,
            "the connection must hold the handshake slot");
        WriteValidHandshakeRequest(connection.FeedInput, new SharpLinkProtocolOptions());
        await YieldUntilAsync(
            () => connection.DisposeCount >= 1 &&
                   harness.Server.ConnectionAdmission.ActiveConnections == 0 &&
                   harness.Server.ConnectionAdmission.ActiveHandshakes == 0,
            "the rejected connection must be closed and release every admission slot");
    }

    private static int CountEntries(CaptureLoggerFactory loggerFactory, int eventId)
        => loggerFactory.Entries.Count(entry => entry.EventId.Id == eventId);

    private static async Task<ServerHarness> StartServerAsync(
        ScriptedListener listener,
        CaptureLoggerFactory loggerFactory,
        ManualTimeProvider timeProvider,
        ISharpLinkServerAuthenticator? authenticator = null)
    {
        var builder = SharpLinkServerBuilder.Create()
            .UseGeneratedManifestSource(FixedGeneratedManifestSource.Empty)
            .DisableAutomaticServiceRegistration()
            .UseTransport(listener)
            .UseLoggerFactory(loggerFactory)
            .UseTimeProvider(timeProvider)
            .UseConnectionAdmission(options =>
            {
                options.MaxConcurrentConnections = 1024;
                options.MaxConcurrentHandshakes = 1024;
            });
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
        await YieldUntilAsync(
            () => server.HealthStatus == SharpLinkHealthStatus.Ready,
            "the scripted server must reach Running");
        return new ServerHarness(server, runTask, runCts);
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

    private static void WriteInvalidMagicFrame(PipeWriter output, ReadOnlySpan<byte> suffix)
    {
        var bytes = new byte[ProtocolV2Constants.HeaderBytes + suffix.Length];
        bytes[0] = 0x5A; // invalid Protocol v2 magic
        suffix.CopyTo(bytes.AsSpan(ProtocolV2Constants.HeaderBytes));
        output.Write(bytes);
        output.FlushAsync().AsTask().GetAwaiter().GetResult();
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

    private static async Task YieldUntilAsync(Func<bool> condition, string failureMessage, int attempts = 2000)
    {
        var deadline = Environment.TickCount64 + 15000;
        for (var attempt = 0; attempt < attempts && !condition(); attempt++)
        {
            if (Environment.TickCount64 >= deadline)
                break;
            if (attempt % 32 == 0)
                await Task.Delay(1);
            else
                await Task.Yield();
        }
        Ensure(condition(), failureMessage);
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

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

    private sealed class TestConnection : ITransportConnection
    {
        private readonly Pipe _inputPipe = new();
        private readonly Pipe _outputPipe = new();
        private int _disposeCount;

        internal TestConnection(string id)
        {
            Id = id;
        }

        public string Id { get; }

        public PipeReader Input => _inputPipe.Reader;

        public PipeWriter Output => _outputPipe.Writer;

        public EndPoint? LocalEndPoint => null;

        public EndPoint? RemoteEndPoint => null;

        internal PipeWriter FeedInput => _inputPipe.Writer;

        internal int DisposeCount => Volatile.Read(ref _disposeCount);

        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            await CompleteAsync(_inputPipe.Writer);
            await CompleteAsync(_outputPipe.Writer);
            await CompleteAsync(_inputPipe.Reader);
            await CompleteAsync(_outputPipe.Reader);
        }

        private static async ValueTask CompleteAsync(PipeWriter writer)
        {
            try
            {
                await writer.CompleteAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }

        private static async ValueTask CompleteAsync(PipeReader reader)
        {
            try
            {
                await reader.CompleteAsync();
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    private sealed class CaptureLoggerFactory : ILoggerFactory
    {
        private readonly Lock _gate = new();

        internal List<CapturedLogEntry> Entries { get; } = [];

        public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);
        public void AddProvider(ILoggerProvider provider) { }
        public void Dispose() { }

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
                    owner.Entries.Add(new CapturedLogEntry(
                        logLevel,
                        eventId,
                        formatter(state, exception),
                        exception));
            }
        }
    }

    private sealed record CapturedLogEntry(LogLevel Level, EventId EventId, string Message, Exception? Exception);

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _runCts;
        private bool _disposed;

        internal ServerHarness(SharpLinkServer server, Task runTask, CancellationTokenSource runCts)
        {
            Server = server;
            RunTask = runTask;
            _runCts = runCts;
        }

        internal SharpLinkServer Server { get; }

        internal Task RunTask { get; }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;
            _disposed = true;
            await StopServerAsync(Server, _runCts, RunTask);
        }
    }
}
