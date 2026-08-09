using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Runtime;
using SharpLink.Sdk;
using SharpLink.Serializer.SharpPack;

[assembly: RpcCodecAdapter(
    typeof(ValueTuple<long, byte[]>),
    typeof(SharpPackRpcCodecAdapter))]

namespace SharpLink.StreamLoadTest;

internal static class EquivalentDuplexWorkload
{
    internal const int DefaultMessageBytes = 4096;
    internal const int DefaultMessagesPerStream = 8;
    internal const int MinimumMessageBytes = sizeof(int);
    internal const int MaximumMessageBytes = 1024 * 1024;
    internal const int MaximumMessagesPerStream = 4096;
    internal const long MaximumPreparedBytes = 64L * 1024 * 1024;

    internal static void ValidateDimensions(int messageBytes, int messagesPerStream)
    {
        if (messageBytes is < MinimumMessageBytes or > MaximumMessageBytes)
            throw new ArgumentOutOfRangeException(nameof(messageBytes));
        if (messagesPerStream is < 1 or > MaximumMessagesPerStream)
            throw new ArgumentOutOfRangeException(nameof(messagesPerStream));
        if ((long)messageBytes * messagesPerStream > MaximumPreparedBytes)
            throw new ArgumentOutOfRangeException(nameof(messagesPerStream), "Prepared payloads must not exceed 64 MiB.");
    }

    internal static byte[][] CreateMessages(int messageBytes, int messagesPerStream)
    {
        ValidateDimensions(messageBytes, messagesPerStream);
        var messages = new byte[messagesPerStream][];
        for (var sequence = 0; sequence < messages.Length; sequence++)
        {
            var message = new byte[messageBytes];
            BinaryPrimitives.WriteInt32LittleEndian(message, sequence);
            for (var index = sizeof(int); index < message.Length; index++)
                message[index] = unchecked((byte)((sequence * 31) + (index * 17)));
            messages[sequence] = message;
        }

        return messages;
    }

    internal static async Task<int> ExecuteValidatedAsync(
        IStreamLoadService rpc,
        long operationId,
        byte[][] messages,
        CancellationToken cancellationToken)
    {
        var responseIndex = 0;
        await foreach (var response in rpc.DuplexEquivalentAsync(
                           operationId,
                           ToStream(messages, cancellationToken))
                           .WithCancellation(cancellationToken))
        {
            if (responseIndex >= messages.Length)
                throw new EquivalentDuplexValidationException("The server returned more messages than requested.");
            if (response.OperationId != operationId)
                throw new EquivalentDuplexValidationException($"Operation ID mismatch at response {responseIndex}.");
            if (response.Payload is null || !response.Payload.AsSpan().SequenceEqual(messages[responseIndex]))
                throw new EquivalentDuplexValidationException($"Payload mismatch at response {responseIndex}.");
            responseIndex++;
        }

        if (responseIndex != messages.Length)
            throw new EquivalentDuplexValidationException($"Expected {messages.Length} responses but received {responseIndex}.");

        return responseIndex;
    }

    internal static async IAsyncEnumerable<byte[]> ToStream(
        byte[][] messages,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var message in messages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return message;
            await Task.CompletedTask;
        }
    }
}

internal sealed class EquivalentDuplexValidationException(string message) : Exception(message);

internal readonly record struct EquivalentDuplexRates(
    double StreamsPerSecond,
    double ErrorRatePercent,
    double MessagesPerSecond,
    double DirectionalBusinessMiBPerSecond)
{
    internal static EquivalentDuplexRates Calculate(
        long completedStreams,
        long failures,
        long validatedMessages,
        double elapsedSeconds,
        int messageBytes)
    {
        if (completedStreams < 0)
            throw new ArgumentOutOfRangeException(nameof(completedStreams));
        if (failures < 0)
            throw new ArgumentOutOfRangeException(nameof(failures));
        if (validatedMessages < 0)
            throw new ArgumentOutOfRangeException(nameof(validatedMessages));
        if (!(elapsedSeconds > 0))
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        if (messageBytes < 0)
            throw new ArgumentOutOfRangeException(nameof(messageBytes));

        var total = completedStreams + failures;
        var messagesPerSecond = validatedMessages / elapsedSeconds;
        return new EquivalentDuplexRates(
            completedStreams / elapsedSeconds,
            total == 0 ? 0 : failures * 100.0 / total,
            messagesPerSecond,
            messagesPerSecond * messageBytes / (1024 * 1024));
    }
}
