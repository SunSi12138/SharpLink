using System.Buffers;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;

namespace SharpLink.UnitTests.Runtime;

public sealed class GeneratedOutputFailureCleanupTests
{
    [Test]
    [Arguments("GetEnumerator")]
    [Arguments("MoveNext")]
    [Arguments("Dispose")]
    public async Task SynchronousOutputFailureMustKeepCauseAndFinishOriginalCleanup(string failureAt)
    {
        var input = new Pipe();
        var output = new Pipe();
        await using var session = RpcSessionTestFixture.CreateSessionOverTestTransport(
            "generated-output-sync-failure", input.Reader, output.Writer, RpcSessionTestFixture.ServerOptions());
        var stream = new FailureStream(failureAt);
        Exception? actual = null;
        try
        {
            await new RpcSessionGeneratedServerBridge(session).PumpOutboundStreamAsync(
                73, 0, stream, new IntCodec(), false, 101, 202, CancellationToken.None);
        }
        catch (Exception error) { actual = error; }
        Ensure(ReferenceEquals(actual, stream.Failure), "normal untimed synchronous failure must retain its exact original cause");
        Ensure(stream.DisposeCount == (failureAt == "GetEnumerator" ? 0 : 1), "only an acquired enumerator disposes, once");
        var source = typeof(CancellationToken).GetField("_source", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stream.Token)!;
        Ensure((bool)typeof(CancellationTokenSource).GetField("_disposed", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!,
            "non-abandoned synchronous failure must release its original linked CTS");
        await output.Reader.CompleteAsync();
        await input.Writer.CompleteAsync();
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class FailureStream(string failureAt) : IAsyncEnumerable<int>, IAsyncEnumerator<int>
    {
        public Exception Failure { get; } = new InvalidOperationException("original output failure: " + failureAt);
        public CancellationToken Token;
        public int DisposeCount;
        public int Current => 7;
        public IAsyncEnumerator<int> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            if (failureAt == "GetEnumerator") throw Failure;
            return this;
        }
        public ValueTask<bool> MoveNextAsync()
        {
            if (failureAt == "MoveNext") throw Failure;
            return ValueTask.FromResult(false);
        }
        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            if (failureAt == "Dispose") throw Failure;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class IntCodec : IRpcCodec<int>
    {
        public void Serialize(in int value, IBufferWriter<byte> buffer) => throw new InvalidOperationException("empty stream");
        public int Deserialize(in ReadOnlySequence<byte> buffer) => throw new InvalidOperationException("no input");
    }
}
