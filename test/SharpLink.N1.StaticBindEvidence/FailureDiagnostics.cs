using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;

namespace SharpLink.N1.StaticBindEvidence;

internal sealed record FixtureFailureShape(string Type, SharpLinkErrorCode? Code, ushort? Detail, string? Stack);
internal sealed record FixtureLogRecord(long Sequence, string Category, LogLevel Level, int Event,
    FixtureFailureShape[] Exceptions);
internal sealed class FailureDiagnosticLoggerFactory : ILoggerFactory
{
    private readonly object _gate = new();
    private readonly List<FixtureLogRecord> _records = [];
    private long _sequence;
    public ILogger CreateLogger(string categoryName) => new DiagnosticLogger(this, categoryName);
    public void AddProvider(ILoggerProvider provider) => throw new NotSupportedException();
    public void Dispose() { }
    internal NonBlockingDiagnostic<object> CaptureNonBlocking() => NonBlockingDiagnostic<object>.Capture(_gate, Capture);
    internal object Capture()
    {
        lock (_gate) return new { total = _sequence, retained = _records.OrderBy(static record => record.Sequence).ToArray(),
            dropped = Math.Max(0, _sequence - _records.Count) };
    }
    private void Record(string category, LogLevel level, EventId eventId, Exception? exception)
    {
        // Never format state, exception.Message or exception.ToString(): these can contain credentials or payloads.
        var errors = new List<FixtureFailureShape>(4);
        for (var current = exception; current is not null && errors.Count != 4; current = current.InnerException)
        {
            var rpc = current as SharpLinkException; var stack = current.StackTrace;
            errors.Add(new(current.GetType().FullName!, rpc?.Code, rpc?.DetailCode,
                stack is { Length: > 4096 } ? stack[..4096] : stack));
        }
        lock (_gate)
        {
            var record = new FixtureLogRecord(++_sequence, category, level, eventId.Id, [.. errors]);
            if (_records.Count < 64) _records.Add(record);
            else _records[32 + (int)((_sequence - 65) % 32)] = record; // first32 + last32
        }
    }
    private sealed class DiagnosticLogger(FailureDiagnosticLoggerFactory owner, string category) : ILogger
    {
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        { if (IsEnabled(logLevel)) owner.Record(category, logLevel, eventId, exception); }
    }
}

internal sealed record FixtureFrameTrace(string Session, bool Outbound, ProtocolV2FrameType Type,
    ProtocolV2FrameFlags Flags, ulong Correlation, int PayloadBytes, ulong? BindingToken);
internal static class FailureDiagnostics
{
    internal static FixtureFrameTrace[] CaptureFrames(TransportFrameGate gate)
        => gate.Frames.Select(static frame => new FixtureFrameTrace(frame.SessionId, frame.Outbound,
            frame.Header.Type, frame.Header.Flags, frame.Header.RequestId, frame.Payload.Length,
            frame.Header.Type == ProtocolV2FrameType.BindAck && frame.Payload.Length == 40
                ? BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload.AsSpan(16))
                : frame.Header.Type == ProtocolV2FrameType.Request && frame.Payload.Length >= 16
                    ? BinaryPrimitives.ReadUInt64LittleEndian(frame.Payload) : null)).ToArray();
    internal static void Write(string phase, object snapshot)
        => Console.Error.WriteLine("N1_FAILURE_SNAPSHOT " + JsonSerializer.Serialize(new { phase, observedUtc = DateTimeOffset.UtcNow, observedTimestamp = System.Diagnostics.Stopwatch.GetTimestamp(), timestampFrequency = System.Diagnostics.Stopwatch.Frequency, snapshot }));
}
