using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using SharpLink.Abstractions;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Compression.Zstd;

namespace Issue739.Features;

[RpcContract]
public interface IFeatureRpc : IService
{
    [NonCancellable] ValueTask<int> AddAsync(int left, int right);
    [NonCancellable] ValueTask<int> FenceAsync();
    ValueTask<int> CancellableAsync(int left, int right, CancellationToken cancellationToken);
    [NonCancellable] ValueTask<byte[]> EchoAsync(byte[] value);
    [NonCancellable] ValueTask<int> UploadAsync(IAsyncEnumerable<int> values);
    [NonCancellable] IAsyncEnumerable<int> DownloadAsync(int count);
    [NonCancellable] IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values);
    [Oneway, NonCancellable] ValueTask NotifyAsync(IAsyncEnumerable<int> values);
}

[RpcService]
public sealed class FeatureService : IFeatureRpc
{
    public bool Suspended, CheckContext, CheckAuthentication, CheckDeadline, CheckCancellation;
    public long CompletedCalls, InputItems, OutputItems, Observations;
    public void Observe(CancellationToken token = default)
    {
        if (CheckContext || CheckAuthentication || CheckDeadline)
        {
            var context = SharpLinkCallContext.Current ?? throw new InvalidOperationException("Missing context");
            if (CheckAuthentication && context.Authentication?.Subject != "issue739-fixture") throw new InvalidOperationException("Missing auth");
            if (CheckDeadline && !context.LocalRpcDeadline.HasValue) throw new InvalidOperationException("Missing deadline");
            Interlocked.Increment(ref Observations);
        }
        if (CheckCancellation && !token.CanBeCanceled) throw new InvalidOperationException("Missing cancellable token");
    }
    public ValueTask<int> FenceAsync() => ValueTask.FromResult(42);
    public ValueTask<int> AddAsync(int left, int right) { Observe(); Interlocked.Increment(ref CompletedCalls); return ValueTask.FromResult(left + right); }
    public ValueTask<int> CancellableAsync(int left, int right, CancellationToken cancellationToken) { Observe(cancellationToken); Interlocked.Increment(ref CompletedCalls); return ValueTask.FromResult(left + right); }
    public ValueTask<byte[]> EchoAsync(byte[] value) { Observe(); Interlocked.Increment(ref CompletedCalls); return ValueTask.FromResult(value); }
    public async ValueTask<int> UploadAsync(IAsyncEnumerable<int> values)
    {
        Observe(); int count = 0, sum = 0;
        await foreach (int value in values.ConfigureAwait(false)) { sum += value; count++; }
        Interlocked.Add(ref InputItems, count); Interlocked.Increment(ref CompletedCalls); return sum;
    }
    public async ValueTask NotifyAsync(IAsyncEnumerable<int> values) { await UploadAsync(values).ConfigureAwait(false); }
    public async IAsyncEnumerable<int> DownloadAsync(int count)
    {
        Observe();
        for (int i = 0; i < count; i++) { if (Suspended) await Task.Yield(); yield return i; }
        Interlocked.Add(ref OutputItems, count); Interlocked.Increment(ref CompletedCalls);
    }
    public async IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values)
    {
        Observe(); int count = 0;
        await foreach (int value in values.ConfigureAwait(false)) { count++; if (Suspended) await Task.Yield(); yield return value; }
        Interlocked.Add(ref InputItems, count); Interlocked.Add(ref OutputItems, count); Interlocked.Increment(ref CompletedCalls);
    }
}

// A new enumerator per call is deliberate fixture cost. Payloads are identical in sync/yield variants.
internal sealed class IntegerProducer(int count, bool suspended) : IAsyncEnumerable<int>
{
    public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default) => new Enumerator(count, suspended);
    private sealed class Enumerator(int count, bool suspended) : IAsyncEnumerator<int>
    {
        private int _index = -1;
        public int Current => _index;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask<bool> MoveNextAsync() => suspended ? MoveSuspended() : ValueTask.FromResult(++_index < count);
        private async ValueTask<bool> MoveSuspended() { await Task.Yield(); return ++_index < count; }
    }
}
internal sealed class ClientHook(bool allocating = false) : ISharpLinkClientInterceptor
{
    public bool Observe = true;
    public long Calls;
    public ValueTask<SharpLinkClientInvocationResult> InvokeAsync(SharpLinkClientInvocationContext context, SharpLinkClientInvocationDelegate next)
    {
        if (Observe) Interlocked.Increment(ref Calls);
        if (allocating) GC.KeepAlive(new byte[128]); // Explicit user-plugin allocation, not framework chain cost.
        return next(context);
    }
}
internal sealed class ServerHook : ISharpLinkServerInterceptor
{
    public bool Observe = true;
    public long Calls;
    public ValueTask InvokeAsync(SharpLinkServerInvocationContext context, SharpLinkServerInvocationDelegate next)
    { if (Observe) Interlocked.Increment(ref Calls); return next(context); }
}
internal sealed class TelemetryScope : IDisposable
{
    private readonly MeterListener? _meter;
    private readonly ActivityListener? _activity;
    public bool Observe = true;
    public long Measurements, ClientActivities, ServerActivities;
    public TelemetryScope(bool metrics, bool tracing)
    {
        if (metrics)
        {
            _meter = new MeterListener();
            _meter.InstrumentPublished = (instrument, listener) => { if (ReferenceEquals(instrument.Meter, SharpLinkTelemetry.Meter)) listener.EnableMeasurementEvents(instrument); };
            _meter.SetMeasurementEventCallback<long>((_, _, _, _) => { if (Observe) Interlocked.Increment(ref Measurements); });
            _meter.SetMeasurementEventCallback<double>((_, _, _, _) => { if (Observe) Interlocked.Increment(ref Measurements); });
            _meter.Start();
        }
        if (tracing)
        {
            // Initialize sources before registering listener; static property assignment happens after source construction.
            var clientSource = SharpLinkTelemetry.ClientActivitySource;
            var serverSource = SharpLinkTelemetry.ServerActivitySource;
            _activity = new ActivityListener {
                ShouldListenTo = source => ReferenceEquals(source, clientSource) || ReferenceEquals(source, serverSource),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => { if (!Observe) return; if (ReferenceEquals(activity.Source, SharpLinkTelemetry.ClientActivitySource)) Interlocked.Increment(ref ClientActivities); else Interlocked.Increment(ref ServerActivities); }
            };
            ActivitySource.AddActivityListener(_activity);
        }
    }
    public void Dispose() { _activity?.Dispose(); _meter?.Dispose(); }
}
internal sealed class CountingZstd : ISharpLinkCompressionProvider
{
    private readonly SharpLinkZstdCompressionProvider _inner = new();
    public long Attempts, Compressed, Decoded;
    public bool Observe = true;
    public string WireProfile => _inner.WireProfile;
    public bool TryCompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes, CancellationToken cancellationToken = default)
    {
        bool result = _inner.TryCompress(input, output, maxOutputBytes, cancellationToken);
        if (Observe) { Interlocked.Increment(ref Attempts); if (result) Interlocked.Increment(ref Compressed); }
        return result;
    }
    public void Decompress(ReadOnlySequence<byte> input, IBufferWriter<byte> output, int maxOutputBytes, CancellationToken cancellationToken = default)
    { _inner.Decompress(input, output, maxOutputBytes, cancellationToken); if (Observe) Interlocked.Increment(ref Decoded); }
}
