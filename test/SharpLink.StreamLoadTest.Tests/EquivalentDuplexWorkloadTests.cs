namespace SharpLink.StreamLoadTest.Tests;

public class EquivalentDuplexWorkloadTests
{
    [Test]
    public async Task ParseEquivalentDuplexArgumentsPreservesExactContract()
    {
        var options = StreamLoadOptions.Parse([
            "--operation", "duplex-equivalent",
            "--message-bytes", "4096",
            "--messages-per-stream", "8",
            "--concurrency", "128",
            "--min-connections", "16",
            "--max-connections", "16"
        ]);

        await Assert.That(options.Operation).IsEqualTo("duplex-equivalent");
        await Assert.That(options.MessageBytes).IsEqualTo(4096);
        await Assert.That(options.MessagesPerStream).IsEqualTo(8);
        await Assert.That(options.ConcurrencyConfig).IsEquivalentTo([128]);
        await Assert.That(options.MinConnections).IsEqualTo(16);
        await Assert.That(options.MaxConnections).IsEqualTo(16);
    }

    [Test]
    public async Task ParseLegacyStreamSizePreservesRc5Defaults()
    {
        var options = StreamLoadOptions.Parse(["--operation", "duplex", "--stream-size", "256"]);

        await Assert.That(options.Operation).IsEqualTo("duplex");
        await Assert.That(options.StreamSize).IsEqualTo(256);
        await Assert.That(options.MessageBytes).IsEqualTo(4096);
        await Assert.That(options.MessagesPerStream).IsEqualTo(8);
        await Assert.That(options.MinConnections).IsEqualTo(1);
        await Assert.That(options.MaxConnections).IsEqualTo(1);
    }

    [Test]
    [Arguments(0, 8)]
    [Arguments(3, 8)]
    [Arguments(1_048_577, 8)]
    [Arguments(4096, 0)]
    [Arguments(4096, 4097)]
    [Arguments(1_048_576, 65)]
    public async Task InvalidEquivalentDuplexBoundsThrow(int messageBytes, int messagesPerStream)
    {
        await Assert.That(() => EquivalentDuplexWorkload.ValidateDimensions(messageBytes, messagesPerStream))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task Eight4096ByteMessagesPreserveOperationSequenceAndPayload()
    {
        const long operationId = 741;
        var messages = EquivalentDuplexWorkload.CreateMessages(4096, 8);
        var rpc = new StreamLoadService();

        var validated = await EquivalentDuplexWorkload.ExecuteValidatedAsync(
            rpc,
            operationId,
            messages,
            CancellationToken.None);

        await Assert.That(validated).IsEqualTo(8);
        await Assert.That(messages.Length).IsEqualTo(8);
        await Assert.That(messages.All(static message => message.Length == 4096)).IsTrue();
        await Assert.That(messages.Distinct(ByteArrayComparer.Instance).Count()).IsEqualTo(8);
    }

    [Test]
    [Arguments(ResponseMutation.WrongOperationId)]
    [Arguments(ResponseMutation.Duplicate)]
    [Arguments(ResponseMutation.Missing)]
    [Arguments(ResponseMutation.Reordered)]
    [Arguments(ResponseMutation.Corrupt)]
    [Arguments(ResponseMutation.Extra)]
    public async Task MutatedResponseIsValidationFailure(ResponseMutation mutation)
    {
        const long operationId = 851;
        var messages = EquivalentDuplexWorkload.CreateMessages(4096, 8);
        var rpc = new MutatingService(mutation);

        await Assert.That(async () => await EquivalentDuplexWorkload.ExecuteValidatedAsync(
                rpc,
                operationId,
                messages,
                CancellationToken.None))
            .Throws<EquivalentDuplexValidationException>();
    }

    [Test]
    public async Task CancellationDoesNotReportPartialStreamAsSuccess()
    {
        var messages = EquivalentDuplexWorkload.CreateMessages(4096, 8);
        using var cancellation = new CancellationTokenSource();
        var rpc = new CancellingService(cancellation);

        await Assert.That(async () => await EquivalentDuplexWorkload.ExecuteValidatedAsync(
                rpc,
                991,
                messages,
                cancellation.Token))
            .Throws<OperationCanceledException>();
        await Assert.That(rpc.ResponsesProduced).IsEqualTo(1);
    }

    [Test]
    public async Task ResultReportsStreamsMessagesAndDirectionalBusinessBytes()
    {
        var rates = EquivalentDuplexRates.Calculate(
            completedStreams: 10,
            failures: 2,
            validatedMessages: 80,
            elapsedSeconds: 2,
            messageBytes: 4096);

        await Assert.That(rates.StreamsPerSecond).IsEqualTo(5);
        await Assert.That(rates.ErrorRatePercent).IsEqualTo(100.0 / 6);
        await Assert.That(rates.MessagesPerSecond).IsEqualTo(40);
        await Assert.That(rates.DirectionalBusinessMiBPerSecond).IsEqualTo(0.15625);
    }

    [Test]
    [NotInParallel]
    public async Task LocalHarnessCompletesExactContractWithZeroFailures()
    {
        await using var harness = await LoadTestTransportFactory.CreateLocalHarness(
            TransportMode.AnonymousPipe,
            "127.0.0.1",
            "127.0.0.1",
            0,
            "/tmp/sharplink-equivalent-duplex-test.sock",
            "sharplink-equivalent-duplex-test",
            10,
            10,
            120,
            1,
            1,
            static builder => builder,
            SharpLinkPerformanceProfile.Throughput);
        using var serverCancellation = new CancellationTokenSource();
        var serverTask = harness.Server.RunAsync(serverCancellation.Token).AsTask();

        try
        {
            await harness.Client.ConnectAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            var rpc = harness.Client.Get<IStreamLoadService>();
            var messages = EquivalentDuplexWorkload.CreateMessages(4096, 8);
            var calls = Enumerable.Range(1, 4)
                .Select(operationId => EquivalentDuplexWorkload.ExecuteValidatedAsync(
                    rpc,
                    operationId,
                    messages,
                    CancellationToken.None))
                .ToArray();

            var validated = await Task.WhenAll(calls).WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(validated).IsEquivalentTo([8, 8, 8, 8]);
            await Assert.That(validated.Sum()).IsEqualTo(32);
        }
        finally
        {
            await serverCancellation.CancelAsync();
            await harness.DisposeServerAsync();
            _ = await Task.WhenAny(serverTask, Task.Delay(1000, CancellationToken.None));
        }
    }

    public enum ResponseMutation
    {
        WrongOperationId,
        Duplicate,
        Missing,
        Reordered,
        Corrupt,
        Extra
    }

    private sealed class MutatingService(ResponseMutation mutation) : IStreamLoadService
    {
        public ValueTask<int> AddAsync(int left, int right) => throw new NotSupportedException();
        public ValueTask<long> UploadAsync(IAsyncEnumerable<int> values) => throw new NotSupportedException();
        public IAsyncEnumerable<int> DownloadAsync(int count) => throw new NotSupportedException();
        public IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values) => throw new NotSupportedException();

        public async IAsyncEnumerable<(long OperationId, byte[] Payload)> DuplexEquivalentAsync(
            long operationId,
            IAsyncEnumerable<byte[]> payloads)
        {
            var messages = new List<byte[]>();
            await foreach (var payload in payloads)
                messages.Add(payload);

            if (mutation == ResponseMutation.WrongOperationId)
                operationId++;
            if (mutation == ResponseMutation.Missing)
                messages.RemoveAt(messages.Count - 1);
            if (mutation == ResponseMutation.Reordered)
                (messages[0], messages[1]) = (messages[1], messages[0]);
            if (mutation == ResponseMutation.Duplicate)
                messages[1] = messages[0];
            if (mutation == ResponseMutation.Corrupt)
            {
                messages[0] = (byte[])messages[0].Clone();
                messages[0][^1] ^= 0xff;
            }
            if (mutation == ResponseMutation.Extra)
                messages.Add(messages[^1]);

            foreach (var message in messages)
                yield return (operationId, message);
        }
    }

    private sealed class CancellingService(CancellationTokenSource cancellation) : IStreamLoadService
    {
        public int ResponsesProduced { get; private set; }

        public ValueTask<int> AddAsync(int left, int right) => throw new NotSupportedException();
        public ValueTask<long> UploadAsync(IAsyncEnumerable<int> values) => throw new NotSupportedException();
        public IAsyncEnumerable<int> DownloadAsync(int count) => throw new NotSupportedException();
        public IAsyncEnumerable<int> DuplexAsync(IAsyncEnumerable<int> values) => throw new NotSupportedException();

        public IAsyncEnumerable<(long OperationId, byte[] Payload)> DuplexEquivalentAsync(
            long operationId,
            IAsyncEnumerable<byte[]> payloads)
            => CancelAfterFirst(operationId, payloads);

        private async IAsyncEnumerable<(long OperationId, byte[] Payload)> CancelAfterFirst(
            long operationId,
            IAsyncEnumerable<byte[]> payloads,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var payload in payloads.WithCancellation(cancellationToken))
            {
                ResponsesProduced++;
                yield return (operationId, payload);
                cancellation.Cancel();
            }
        }
    }

    private sealed class ByteArrayComparer : IEqualityComparer<byte[]>
    {
        internal static readonly ByteArrayComparer Instance = new();

        public bool Equals(byte[]? left, byte[]? right)
            => ReferenceEquals(left, right) || left is not null && right is not null && left.AsSpan().SequenceEqual(right);

        public int GetHashCode(byte[] value)
        {
            var hash = new HashCode();
            hash.AddBytes(value);
            return hash.ToHashCode();
        }
    }
}
