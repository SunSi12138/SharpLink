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

internal sealed class ChaosOptions
{
    internal TimeSpan Duration { get; private init; } = TimeSpan.FromSeconds(120);
    internal int Concurrency { get; private init; } = 32;
    internal TimeSpan RestartInterval { get; private init; } = TimeSpan.FromSeconds(5);
    internal TimeSpan CheckpointInterval { get; private init; } = TimeSpan.FromSeconds(30);
    internal bool DumpOnFailure { get; private init; } = true;
    internal bool StopOnUnexpectedFailure { get; private init; } = true;
    internal bool InjectClientError { get; private init; }
    internal bool InjectServerError { get; private init; }
    internal bool InjectUnobservedTaskException { get; private init; }
    internal ChaosTransport Transport { get; private init; } = ChaosTransport.Tcp;
    internal string SharedMemoryName { get; private init; } = "sharplink-chaos";
    internal string? JsonOutputPath { get; private init; }

    internal static ChaosOptions Parse(string[] args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (!argument.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{argument}'.");
            if (++index >= args.Length)
                throw new ArgumentException($"Missing value for '{argument}'.");
            values[argument[2..]] = args[index];
        }

        if (values.ContainsKey("duration") && values.ContainsKey("duration-seconds"))
            throw new ArgumentException("Use either --duration or --duration-seconds, not both.");
        var duration = values.TryGetValue("duration", out var durationText)
            ? ParseDuration(durationText, "duration")
            : TimeSpan.FromSeconds(ParsePositive(values, "duration-seconds", 120));
        var concurrency = ParsePositive(values, "concurrency", 32);
        var restartSeconds = ParsePositive(values, "restart-interval-seconds", 5);
        var transport = values.GetValueOrDefault("transport", "tcp").ToLowerInvariant() switch
        {
            "tcp" => ChaosTransport.Tcp,
            "sharedmemory" or "shared-memory" or "shm" => ChaosTransport.SharedMemory,
            var value => throw new ArgumentException($"Unsupported chaos transport '{value}'.")
        };
        if (TimeSpan.FromSeconds(restartSeconds) >= duration)
            throw new ArgumentException("Restart interval must be shorter than the chaos duration.");
        var checkpointInterval = values.TryGetValue("checkpoint-interval", out var checkpointText)
            ? ParseDuration(checkpointText, "checkpoint-interval")
            : values.TryGetValue("checkpoint-interval-seconds", out var checkpointSecondsText)
                ? TimeSpan.FromSeconds(ParsePositive(checkpointSecondsText, "checkpoint-interval-seconds"))
                : GetDefaultCheckpointInterval(duration);
        if (checkpointInterval >= duration)
            checkpointInterval = TimeSpan.FromTicks(Math.Max(TimeSpan.TicksPerSecond, duration.Ticks / 2));
        return new ChaosOptions
        {
            Duration = duration,
            Concurrency = concurrency,
            RestartInterval = TimeSpan.FromSeconds(restartSeconds),
            CheckpointInterval = checkpointInterval,
            DumpOnFailure = ParseBoolean(values, "dump-on-failure", fallback: true),
            StopOnUnexpectedFailure = ParseBoolean(values, "stop-on-unexpected", fallback: true),
            InjectClientError = ParseBoolean(values, "inject-client-error", fallback: false),
            InjectServerError = ParseBoolean(values, "inject-server-error", fallback: false),
            InjectUnobservedTaskException = ParseBoolean(
                values,
                "inject-unobserved-task-exception",
                fallback: false),
            Transport = transport,
            SharedMemoryName = values.GetValueOrDefault("shm-name", "sharplink-chaos"),
            JsonOutputPath = values.GetValueOrDefault("json-output")
        };
    }

    private static int ParsePositive(Dictionary<string, string> values, string name, int fallback)
    {
        var value = int.Parse(values.GetValueOrDefault(name, fallback.ToString()));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return value;
    }

    private static int ParsePositive(string text, string name)
    {
        var value = int.Parse(text);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value, name);
        return value;
    }

    private static TimeSpan ParseDuration(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var unitLength = char.IsLetter(value[^1]) ? 1 : 0;
        if (unitLength == 1 && double.TryParse(
                value.AsSpan(0, value.Length - 1),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var amount))
        {
            var duration = char.ToLowerInvariant(value[^1]) switch
            {
                's' => TimeSpan.FromSeconds(amount),
                'm' => TimeSpan.FromMinutes(amount),
                'h' => TimeSpan.FromHours(amount),
                'd' => TimeSpan.FromDays(amount),
                _ => throw new ArgumentException(
                    $"Unsupported {name} unit in '{value}'. Use s, m, h, d, or a TimeSpan.",
                    name)
            };
            if (duration > TimeSpan.Zero)
                return duration;
        }
        if (TimeSpan.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, out var parsed) &&
            parsed > TimeSpan.Zero)
        {
            return parsed;
        }
        throw new ArgumentException($"{name} must be a positive duration such as 10m, 24h, or 00:10:00.", name);
    }

    private static TimeSpan GetDefaultCheckpointInterval(TimeSpan duration)
    {
        if (duration >= TimeSpan.FromHours(12))
            return TimeSpan.FromMinutes(30);
        if (duration >= TimeSpan.FromHours(6))
            return TimeSpan.FromMinutes(15);
        if (duration >= TimeSpan.FromHours(1))
            return TimeSpan.FromMinutes(10);
        if (duration >= TimeSpan.FromMinutes(10))
            return TimeSpan.FromMinutes(1);
        if (duration >= TimeSpan.FromMinutes(2))
            return TimeSpan.FromSeconds(30);
        return TimeSpan.FromSeconds(10);
    }

    private static bool ParseBoolean(
        IReadOnlyDictionary<string, string> values,
        string name,
        bool fallback)
    {
        if (!values.TryGetValue(name, out var value))
            return fallback;
        if (bool.TryParse(value, out var parsed))
            return parsed;
        throw new ArgumentException($"{name} must be true or false.", name);
    }
}
