using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Client;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Server;

[assembly: RpcCodecAdapterRegistration(
    typeof(SharpLink.PreCreditAotSmoke.PreCreditPayloadCodecAdapter),
    "sharplink.precredit-aot.unsized",
    "sharplink.precredit-aot.unsized.v1")]

namespace SharpLink.PreCreditAotSmoke;

public static class Program
{
    private const int PayloadBytes = 64 * 1024;
    private const int FlowWindowBytes = 2 * PayloadBytes;

    public static async Task<int> Main(string[] args)
    {
        var useSharedMemory = args.Any(static value =>
            value.Equals("sharedmemory", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("shared-memory", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("shm", StringComparison.OrdinalIgnoreCase));
        var transportName = useSharedMemory ? "sharedmemory" : "tcp";
        var sharedMemoryName = $"sharplink-precredit-aot-{Environment.ProcessId}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = timeout.Token;
        PreCreditProbe.Reset();

        var serverBuilder = SharpLinkServerBuilder.Create()
            .UseRuntime(ConfigureRuntime);
        if (useSharedMemory)
            serverBuilder.UseSharedMemory(sharedMemoryName);
        else
            serverBuilder.UseTcp(0, IPAddress.Loopback.ToString());

        var port = useSharedMemory
            ? 0
            : ((IPEndPoint)serverBuilder.Transport!.LocalEndPoint!).Port;
        var server = serverBuilder.Build();
        var serverTask = Task.Run(async () =>
        {
            try
            {
                await server.RunAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }, CancellationToken.None);

        ISharpLinkClient client;
        if (useSharedMemory)
        {
            client = SharpClientBuilder.Create()
                .UseRuntime(ConfigureRuntime)
                .UseSharedMemory(sharedMemoryName)
                .Build();
        }
        else
        {
            client = SharpClientBuilder.Create()
                .UseRuntime(ConfigureRuntime)
                .UseTcp(IPAddress.Loopback.ToString(), port)
                .Build();
        }

        try
        {
            await client.ConnectAsync(cancellationToken);
            var service = client.Get<IPreCreditAotService>();
            var enumerator = service.StreamAsync(8).GetAsyncEnumerator(cancellationToken);
            var disposed = false;
            try
            {
                if (!await enumerator.MoveNextAsync() || enumerator.Current.Sequence != 0)
                    throw new InvalidOperationException("The pre-credit smoke did not receive the first stream item.");

                await PreCreditProbe.FourthSerialized.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

                // The 128 KiB receive window initially admits two 64 KiB items. Consuming the
                // first item returns exactly one item's credit, which admits the third. The fourth
                // custom-unsized item has already serialized but must now be waiting for credit,
                // so the server iterator cannot advance to its fifth MoveNext yet.
                if (PreCreditProbe.FifthMoveNextStarted.Task.IsCompleted)
                {
                    throw new InvalidOperationException(
                        "The server advanced past the fourth item before additional receive credit was returned.");
                }

                if (!await enumerator.MoveNextAsync() || enumerator.Current.Sequence != 1)
                    throw new InvalidOperationException("The pre-credit smoke did not receive the second stream item.");

                // Returning the second item's credit must release the fourth blocked send and let
                // the generated server pump request the fifth item. This is a controlled
                // WindowUpdate transition; no timing sleep is used to establish backpressure.
                await PreCreditProbe.FifthMoveNextStarted.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

                await enumerator.DisposeAsync();
                disposed = true;
                await PreCreditProbe.StreamDisposed.Task
                    .WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

                if (await service.PingAsync() != 42)
                    throw new InvalidOperationException("The connection was not reusable after pre-credit cancellation.");

                Console.WriteLine($"PRE_CREDIT_AOT_PASS transport={transportName}");
                return 0;
            }
            finally
            {
                if (!disposed)
                {
                    try
                    {
                        await enumerator.DisposeAsync();
                    }
                    catch
                    {
                    }
                }
            }
        }
        catch (Exception exception)
        {
            await Console.Error.WriteLineAsync($"PRE_CREDIT_AOT_FAIL transport={transportName}: {exception}");
            return 1;
        }
        finally
        {
            await timeout.CancelAsync();
            await client.DisposeAsync();
            await server.DisposeAsync();
            await Task.WhenAny(serverTask, Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None));
        }
    }

    private static void ConfigureRuntime(SharpLinkRuntimeOptions options)
    {
        options.FlowControl.StreamReceiveWindowBytes = FlowWindowBytes;
        options.FlowControl.ConnectionReceiveWindowBytes = FlowWindowBytes;
        options.FlowControl.MaxPreCreditSerializedBytes = PayloadBytes;
    }
}

[RpcContract]
public interface IPreCreditAotService : IService
{
    [NonCancellable]
    IAsyncEnumerable<PreCreditPayload> StreamAsync(int count);

    [NonCancellable]
    ValueTask<int> PingAsync();
}

[RpcService]
public sealed class PreCreditAotService : IPreCreditAotService
{
    public async IAsyncEnumerable<PreCreditPayload> StreamAsync(int count)
    {
        try
        {
            for (var index = 0; index < count; index++)
            {
                if (index == 4)
                    PreCreditProbe.FifthMoveNextStarted.TrySetResult(true);
                await Task.Yield();
                yield return new PreCreditPayload(index);
            }
        }
        finally
        {
            PreCreditProbe.StreamDisposed.TrySetResult(true);
        }
    }

    public ValueTask<int> PingAsync() => ValueTask.FromResult(42);
}

[RpcCodecAdapter(typeof(PreCreditPayloadCodecAdapter))]
public readonly record struct PreCreditPayload(int Sequence);

public sealed class PreCreditPayloadCodecAdapter : IRpcCodecAdapter
{
    public string AdapterId => "sharplink.precredit-aot.unsized";

    public string WireFormatId => "sharplink.precredit-aot.unsized.v1";

    public IRpcCodecAdapterScope CreateScope() => new Scope();

    private sealed class Scope : IRpcCodecAdapterScope
    {
        public IRpcCodec<T> CreateCodec<T>()
        {
            if (typeof(T) != typeof(PreCreditPayload))
                throw new InvalidOperationException($"Unsupported pre-credit AOT codec type: {typeof(T)}.");
            return (IRpcCodec<T>)(object)new PreCreditPayloadCodec();
        }

        public void Dispose()
        {
        }
    }
}

internal sealed class PreCreditPayloadCodec : IRpcCodec<PreCreditPayload>
{
    public void Serialize(in PreCreditPayload value, IBufferWriter<byte> buffer)
    {
        var span = buffer.GetSpan(64 * 1024)[..(64 * 1024)];
        BinaryPrimitives.WriteInt32LittleEndian(span, value.Sequence);
        span[sizeof(int)..].Fill(0x5a);
        buffer.Advance(span.Length);
        PreCreditProbe.RecordSerialized();
    }

    public PreCreditPayload Deserialize(in ReadOnlySequence<byte> buffer)
    {
        if (buffer.Length != 64 * 1024)
        {
            throw new SharpLinkException(
                SharpLinkErrorCode.DataLoss,
                $"Unexpected pre-credit smoke payload size {buffer.Length}.");
        }

        Span<byte> header = stackalloc byte[sizeof(int)];
        buffer.Slice(0, sizeof(int)).CopyTo(header);
        return new PreCreditPayload(BinaryPrimitives.ReadInt32LittleEndian(header));
    }
}

internal static class PreCreditProbe
{
    internal static TaskCompletionSource<bool> FourthSerialized { get; private set; } = CreateSignal();

    internal static TaskCompletionSource<bool> FifthMoveNextStarted { get; private set; } = CreateSignal();

    internal static TaskCompletionSource<bool> StreamDisposed { get; private set; } = CreateSignal();

    private static int _serializedCount;

    internal static void Reset()
    {
        Volatile.Write(ref _serializedCount, 0);
        FourthSerialized = CreateSignal();
        FifthMoveNextStarted = CreateSignal();
        StreamDisposed = CreateSignal();
    }

    internal static void RecordSerialized()
    {
        if (Interlocked.Increment(ref _serializedCount) >= 4)
            FourthSerialized.TrySetResult(true);
    }

    private static TaskCompletionSource<bool> CreateSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
