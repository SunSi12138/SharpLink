using System.Buffers;
using SharpLink.Serializer.SharpPack;
using SharpPack;

namespace SharpLink.UnitTests.Runtime;

public class SharpPackGeneratedScopeConfigurationTests
{
    [Test]
    public void GeneratedFormatterConfigurationShouldBeScopeOwnedAndFrozenBeforeCodecCreation()
    {
        using var firstScope = new SharpPackRpcCodecAdapter().CreateScope();
        using var secondScope = new SharpPackRpcCodecAdapter().CreateScope();
        var firstConfiguration = (ISharpPackRpcCodecAdapterScopeConfiguration)firstScope;
        var secondConfiguration = (ISharpPackRpcCodecAdapterScopeConfiguration)secondScope;
        var firstFormatter = new GeneratedScopeValueFormatter(11);
        var secondFormatter = new GeneratedScopeValueFormatter(29);

        firstConfiguration.Configure("tests/first", builder =>
            builder.Register<GeneratedScopeValue>(firstFormatter));
        secondConfiguration.Configure("tests/second", builder =>
            builder.Register<GeneratedScopeValue>(secondFormatter));

        var firstCodec = firstScope.CreateCodec<GeneratedScopeValue>();
        var secondCodec = secondScope.CreateCodec<GeneratedScopeValue>();
        var firstWriter = new ArrayBufferWriter<byte>();
        var secondWriter = new ArrayBufferWriter<byte>();
        firstCodec.Serialize(new GeneratedScopeValue { Value = 5 }, firstWriter);
        secondCodec.Serialize(new GeneratedScopeValue { Value = 5 }, secondWriter);
        var firstDecoded = firstCodec.Deserialize(new ReadOnlySequence<byte>(firstWriter.WrittenMemory));
        var secondDecoded = secondCodec.Deserialize(new ReadOnlySequence<byte>(secondWriter.WrittenMemory));

        Ensure(firstDecoded is { Value: 16 }, "first Scope uses its generated formatter graph");
        Ensure(secondDecoded is { Value: 34 }, "second Scope uses a different generated formatter graph");
        Ensure(firstFormatter.SerializeCount == 1 && firstFormatter.DeserializeCount == 1,
            "first formatter is used only by first Scope");
        Ensure(secondFormatter.SerializeCount == 1 && secondFormatter.DeserializeCount == 1,
            "second formatter is used only by second Scope");

        var duplicateInvoked = false;
        firstConfiguration.Configure("tests/first", _ => duplicateInvoked = true);
        Ensure(!duplicateInvoked, "same generated configuration is idempotent");

        ExpectInvalidOperation(() => firstConfiguration.Configure("tests/conflict", _ => { }));
    }

    [Test]
    public void GeneratedFormatterConfigurationShouldRejectLateInstallation()
    {
        using var scope = new SharpPackRpcCodecAdapter().CreateScope();
        _ = scope.CreateCodec<int>();
        var configuration = (ISharpPackRpcCodecAdapterScopeConfiguration)scope;

        ExpectInvalidOperation(() => configuration.Configure("tests/late", _ => { }));
    }

    private static void ExpectInvalidOperation(Action action)
    {
        try
        {
            action();
            throw new Exception("expected InvalidOperationException");
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}

public sealed class GeneratedScopeValue
{
    public int Value { get; set; }
}

internal sealed class GeneratedScopeValueFormatter(int offset) : SharpPackFormatter<GeneratedScopeValue>
{
    internal int SerializeCount { get; private set; }
    internal int DeserializeCount { get; private set; }

    public override void Serialize<TBufferWriter>(
        ref SharpPackWriter<TBufferWriter> writer,
        scoped ref GeneratedScopeValue? value)
    {
        SerializeCount++;
        writer.WriteObjectHeader(1);
        writer.WriteUnmanaged((value?.Value ?? 0) + offset);
    }

    public override void Deserialize(
        ref SharpPackReader reader,
        scoped ref GeneratedScopeValue? value)
    {
        DeserializeCount++;
        if (!reader.TryReadObjectHeader(out var count))
        {
            value = null;
            return;
        }
        if (count != 1)
            SharpPackSerializationException.ThrowInvalidPropertyCount(1, count);
        value = new GeneratedScopeValue { Value = reader.ReadUnmanaged<int>() };
    }
}
