using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.IntegrationTests;

/// <summary>
/// Test-only, per-case evidence recorder for issue #998. It does not participate in RPC routing.
/// </summary>
internal sealed class SessionControlStudyAttempt : IDisposable
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly string _directory;
    private readonly string _caseId;
    private readonly string _variant;
    private long _sequence;
    private bool _completed;

    private SessionControlStudyAttempt(string directory, string caseId, string variant)
    {
        _directory = directory;
        _caseId = caseId;
        _variant = variant;
        Directory.CreateDirectory(directory);
    }

    internal static SessionControlStudyAttempt Begin(string caseId, string? variant = null)
    {
        variant ??= Environment.GetEnvironmentVariable("STUDY_VARIANT") ?? "S";
        var root = FindRepositoryRoot();
        var attempt = Environment.GetEnvironmentVariable("STUDY_ATTEMPT") ?? "attempt-1";
        var directory = Path.Combine(
            root,
            "artifacts",
            "session-control-study",
            variant,
            caseId,
            attempt);
        var recorder = new SessionControlStudyAttempt(directory, caseId, variant);
        recorder.Emit("attempt-started", new Dictionary<string, object?>
        {
            ["variant"] = variant,
            ["sourceSha"] = SourceSha,
            ["baseSha"] = BaseSha,
            ["transport"] = "tcp",
            ["os"] = Environment.OSVersion.ToString(),
            ["runtime"] = Environment.Version.ToString()
        });
        return recorder;
    }

    internal string Variant => _variant;

    internal string DirectoryPath => _directory;

    internal static string SourceSha
        => Environment.GetEnvironmentVariable("STUDY_SOURCE_SHA") ?? "UNKNOWN";

    internal static string BaseSha
        => Environment.GetEnvironmentVariable("STUDY_BASE_SHA") ?? "2948de82fff77d66fce51ecec9a5783b6ef3cb62";

    internal void Emit(string name, object? payload = null)
    {
        lock (_gate)
        {
            var entry = new Dictionary<string, object?>
            {
                ["seq"] = Interlocked.Increment(ref _sequence),
                ["case"] = _caseId,
                ["variant"] = _variant,
                ["event"] = name,
                ["monotonicMs"] = Math.Round(_clock.Elapsed.TotalMilliseconds, 3),
                ["utc"] = DateTimeOffset.UtcNow.ToString("O"),
                ["payload"] = payload
            };
            File.AppendAllText(
                Path.Combine(_directory, "events.jsonl"),
                JsonSerializer.Serialize(entry) + Environment.NewLine);
        }
    }

    internal void Complete(string status, string? detail = null)
    {
        lock (_gate)
        {
            if (_completed)
                return;
            _completed = true;
            var result = new Dictionary<string, object?>
            {
                ["schema"] = 1,
                ["study"] = "issue-775-session-control-alternative",
                ["variant"] = _variant,
                ["case"] = _caseId,
                ["sourceSha"] = SourceSha,
                ["baseSha"] = BaseSha,
                ["os"] = Environment.OSVersion.ToString(),
                ["runtime"] = Environment.Version.ToString(),
                ["transport"] = "tcp",
                ["attempt"] = 1,
                ["status"] = status,
                ["detail"] = detail,
                ["eventCount"] = _sequence,
                ["evidencePaths"] = new[] { "events.jsonl", "result.json" }
            };
            File.WriteAllText(
                Path.Combine(_directory, "result.json"),
                JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public void Dispose() => Complete("INCONCLUSIVE", "Test exited without an explicit result.");

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "Sharplink.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("SharpLink repository root was not found.");
    }
}

internal static class SessionControlStudySupport
{
    internal static string[] ReadySessionIds(ISharpLinkClient client)
    {
        var field = typeof(SharpLinkClient).GetField(
            "_readyConnections",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(SharpLinkClient).FullName, "_readyConnections");
        var connections = (ClientConnection[]?)field.GetValue(client) ?? [];
        return connections
            .Where(static connection => connection.CanAcceptCalls)
            .Select(static connection => connection.Session.Id)
            .ToArray();
    }

    internal static bool TryInstallSNotificationHooks(
        SharpLinkServer server,
        TaskCompletionSource cutCommitted,
        TaskCompletionSource releaseNotification)
    {
        var cut = typeof(SharpLinkServer).GetProperty(
            "PinnedSessionCutCommittedForTesting",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        var barrier = typeof(SharpLinkServer).GetProperty(
            "PinnedSessionRetirementNotificationBarrierForTesting",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (cut is null || barrier is null)
            return false;

        cut.SetValue(server, new Action(() => cutCommitted.TrySetResult()));
        barrier.SetValue(server, new Func<Task>(() => releaseNotification.Task));
        return true;
    }

    internal static void ClearSNotificationHooks(SharpLinkServer server)
    {
        typeof(SharpLinkServer).GetProperty(
                "PinnedSessionCutCommittedForTesting",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(server, null);
        typeof(SharpLinkServer).GetProperty(
                "PinnedSessionRetirementNotificationBarrierForTesting",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(server, null);
    }

    internal static async Task WaitForReadySessionChangeAsync(
        ISharpLinkClient client,
        string oldSessionId,
        TimeSpan timeout)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * timeout.TotalSeconds);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            var ids = ReadySessionIds(client);
            if (ids.Length > 0 && ids.All(id => !string.Equals(id, oldSessionId, StringComparison.Ordinal)))
                return;
            await Task.Delay(10);
        }
        throw new TimeoutException($"No replacement Ready session appeared for old session {oldSessionId}.");
    }
}
