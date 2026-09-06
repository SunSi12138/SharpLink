using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

namespace SharpLink.ChaosTests;

internal sealed class ChaosMetricObserver : IDisposable
{
    private static readonly string[] Tracked =
    [
        "sharplink.connections.active",
        "sharplink.calls.active",
        "sharplink.requests.pending",
        "sharplink.streams.active",
        "sharplink.send.queue.bytes"
    ];

    private readonly ConcurrentDictionary<string, long> _values = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<ActiveCallKey, long> _activeCallBreakdown = new();
    private readonly MeterListener _listener = new();

    internal ChaosMetricObserver()
    {
        for (var index = 0; index < Tracked.Length; index++)
            _values[Tracked[index]] = 0;
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "SharpLink" && _values.ContainsKey(instrument.Name))
                listener.EnableMeasurementEvents(instrument);
        };
        _listener.SetMeasurementEventCallback<long>((instrument, measurement, tags, _) =>
        {
            _values.AddOrUpdate(
                instrument.Name,
                static (_, delta) => delta,
                static (_, value, delta) => value + delta,
                measurement);
            if (instrument.Name != "sharplink.calls.active")
                return;

            var side = "unknown";
            var contractId = long.MinValue;
            var methodId = long.MinValue;
            foreach (var tag in tags)
            {
                switch (tag.Key)
                {
                    case "rpc.side":
                        if (tag.Value is string configuredSide)
                            side = configuredSide;
                        break;
                    case "rpc.sharplink.contract_id":
                        _ = TryReadInt64(tag.Value, out contractId);
                        break;
                    case "rpc.sharplink.method_id":
                        _ = TryReadInt64(tag.Value, out methodId);
                        break;
                }
            }
            var key = new ActiveCallKey(side, contractId, methodId);
            _activeCallBreakdown.AddOrUpdate(
                key,
                static (_, delta) => delta,
                static (_, value, delta) => value + delta,
                measurement);
        });
        _listener.Start();

    }

    internal IReadOnlyDictionary<string, long> Snapshot()
        => _values.ToDictionary(static value => value.Key, static value => value.Value);

    internal IReadOnlyDictionary<string, long> ActiveCallBreakdownSnapshot()
        => _activeCallBreakdown
            .Where(static value => value.Value != 0)
            .ToDictionary(
                static value => value.Key.ToString(),
                static value => value.Value,
                StringComparer.Ordinal);

    private static bool TryReadInt64(object? value, out long result)
    {
        switch (value)
        {
            case long signed:
                result = signed;
                return true;
            case ulong unsigned when unsigned <= long.MaxValue:
                result = (long)unsigned;
                return true;
            case int signed32:
                result = signed32;
                return true;
            case uint unsigned32:
                result = unsigned32;
                return true;
            default:
                result = long.MinValue;
                return false;
        }
    }

    internal async Task<ChaosDrainResult> WaitForZeroAsync(TimeSpan timeout)
    {
        var started = Stopwatch.GetTimestamp();
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (_values.Any(static value => value.Value != 0))
        {
            if (Stopwatch.GetTimestamp() >= deadline)
            {
                return CreateDrainResult(drained: false, started);
            }
            await Task.Delay(20).ConfigureAwait(false);
        }
        return CreateDrainResult(drained: true, started);
    }

    private ChaosDrainResult CreateDrainResult(bool drained, long started)
        => new(
            drained,
            Stopwatch.GetElapsedTime(started).TotalSeconds,
            Snapshot(),
            ActiveCallBreakdownSnapshot());

    public void Dispose() => _listener.Dispose();

    private readonly record struct ActiveCallKey(string Side, long ContractId, long MethodId)
    {
        public override string ToString()
            => $"{Side}:{FormatIdentifier(ContractId)}:{FormatIdentifier(MethodId)}";

        private static string FormatIdentifier(long value)
            => value == long.MinValue
                ? "unknown"
                : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }
}

internal sealed class ChaosLoggerFactory : ILoggerFactory, ILogger
{
    private const int MaxRetainedErrors = 8;
    private readonly ConcurrentQueue<string> _generationErrors = new();
    private readonly ConcurrentQueue<string> _allErrors = new();
    private long _errorCount;

    internal long ErrorCount => Volatile.Read(ref _errorCount);

    public void AddProvider(ILoggerProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
    }

    public ILogger CreateLogger(string categoryName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
        return this;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        RecordError(
            $"Event={eventId.Id}:{eventId.Name}; Message={formatter(state, exception)}; " +
            $"Exception={exception}");
    }

    internal void Clear() => _generationErrors.Clear();

    internal IReadOnlyList<string> Snapshot() => [.. _generationErrors];

    internal IReadOnlyList<string> AllSnapshot() => [.. _allErrors];

    internal void InjectErrorForGateProbe(string owner)
        => RecordError($"Injected {owner} Error for the Chaos release-gate self-test.");

    private void RecordError(string error)
    {
        Interlocked.Increment(ref _errorCount);
        EnqueueBounded(_generationErrors, error);
        EnqueueBounded(_allErrors, error);
    }

    private static void EnqueueBounded(ConcurrentQueue<string> queue, string error)
    {
        queue.Enqueue(error);
        while (queue.Count > MaxRetainedErrors)
            queue.TryDequeue(out _);
    }

    public void Dispose()
    {
        _generationErrors.Clear();
        _allErrors.Clear();
    }
}

internal sealed record ChaosDrainResult(
    bool Drained,
    double WaitedSeconds,
    IReadOnlyDictionary<string, long> Metrics,
    IReadOnlyDictionary<string, long> ActiveCallBreakdown)
{
    internal string Describe()
        => "SharpLink state did not drain after chaos: " +
           string.Join(", ", Metrics.Select(static value => $"{value.Key}={value.Value}")) +
           "; active-call breakdown: " +
           string.Join(", ", ActiveCallBreakdown.Select(static value => $"{value.Key}={value.Value}"));
}
