using System.Reflection;
using System.Threading.Tasks.Sources;
using SharpLink.Client;

namespace SharpLink.UnitTests.Client;

internal static class ClientStreamWaitTestSupport
{
    private const BindingFlags Members = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    internal static IAsyncEnumerator<string> Wrap(
        SharpLinkClient client, IAsyncEnumerator<string> source, string wrapper,
        SharpLinkTelemetry.CallScope scope = default, List<object>? resultSources = null)
    {
        var reader = source;
        if (wrapper is "telemetry" or "both")
        {
            var type = typeof(SharpLinkClient).GetNestedType("TelemetryAsyncEnumerator`1", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(string));
            reader = (IAsyncEnumerator<string>)Activator.CreateInstance(type, Members, null, [reader, scope], null)!;
        }
        if (wrapper is "logical" or "both")
        {
            typeof(SharpLinkClient).GetField("_activeLogicalInvocations", Members)!.SetValue(client, 1);
            var type = typeof(SharpLinkClient).GetNestedType("LogicalInvocationAsyncEnumerable`1", BindingFlags.NonPublic)!
                .MakeGenericType(typeof(string));
            var stream = (IAsyncEnumerable<string>)Activator.CreateInstance(type, Members, null,
                [client, new CapturingEnumerator(reader, wrapper == "both" ? resultSources : null)], null)!;
            reader = stream.GetAsyncEnumerator();
        }
        return reader;
    }

    internal static object ResultSource(ValueTask<bool> read)
        => typeof(ValueTask<bool>).GetField("_obj", Members)!.GetValue(read)
            ?? throw new InvalidOperationException("pending wrapper has no result source");

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class CapturingEnumerator(IAsyncEnumerator<string> reader, List<object>? sources)
        : IAsyncEnumerable<string>, IAsyncEnumerator<string>
    {
        public string Current => reader.Current;
        public IAsyncEnumerator<string> GetAsyncEnumerator(CancellationToken cancellationToken = default) => this;
        public ValueTask<bool> MoveNextAsync()
        {
            var read = reader.MoveNextAsync();
            sources?.Add(ResultSource(read));
            return read;
        }
        public ValueTask DisposeAsync() => reader.DisposeAsync();
    }

    internal sealed class PendingStream(object? owner = null) : IAsyncEnumerator<string>, IValueTaskSource<bool>
    {
        private ManualResetValueTaskSourceCore<bool> _core = new() { RunContinuationsAsynchronously = false };
        private bool _pending;
        internal int ConsumptionCount { get; private set; }
        internal int DisposeCount { get; private set; }
        public string Current => "value";
        public ValueTask<bool> MoveNextAsync()
        {
            Require(!_pending, "only one pending source operation is allowed");
            _pending = true;
            return new ValueTask<bool>(this, _core.Version);
        }
        internal void Finish(bool result)
        {
            Require(_pending, "source was not pending");
            _pending = false;
            _core.SetResult(result);
        }
        internal void Fail(Exception error)
        {
            Require(_pending, "source was not pending");
            _pending = false;
            _core.SetException(error);
        }
        internal void ReleaseForCleanup() { if (_pending) Finish(false); }
        public ValueTask DisposeAsync() { DisposeCount++; GC.KeepAlive(owner); return ValueTask.CompletedTask; }
        public bool GetResult(short token)
        {
            Require(token == _core.Version && ConsumptionCount == 0, "source was consumed twice");
            try { return _core.GetResult(token); }
            finally { ConsumptionCount++; _core.Reset(); }
        }
        public ValueTaskSourceStatus GetStatus(short token) => _core.GetStatus(token);
        public void OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags)
            => _core.OnCompleted(continuation, state, token, flags);
    }
}
