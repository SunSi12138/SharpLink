using System.Collections.Generic;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkCallOptionsTests
{
    [Test]
    public void CallOptionsShouldNotExposeAnUnusableCompressionSwitch()
    {
        var property = typeof(SharpLinkCallOptions).GetProperty("EnableCompression");

        Ensure(property is null,
            "compression is negotiated and applied automatically, so call options must not expose a switch that always fails");
    }

    [Test]
    public void MetadataShouldBeImmutableAndPreserveInsertionOrder()
    {
        var entries = new[]
        {
            new KeyValuePair<string, string>("tenant", "factory-a"),
            new KeyValuePair<string, string>("trace", "42")
        };

        var metadata = new SharpLinkMetadata(entries);
        entries[0] = new KeyValuePair<string, string>("tenant", "mutated");

        Ensure(metadata.Count == 2, "metadata count");
        Ensure(metadata[0].Key == "tenant" && metadata[0].Value == "factory-a", "metadata snapshot");
    }

    [Test]
    public void MetadataShouldRejectEmptyKeys()
    {
        try
        {
            _ = new SharpLinkMetadata(new KeyValuePair<string, string>(string.Empty, "value"));
            throw new Exception("expected invalid metadata key");
        }
        catch (ArgumentException)
        {
        }
    }

    [Test]
    public void MetadataShouldRejectInvalidUnicodeBeforeWireEncoding()
    {
        var invalidKey = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant\uD800", "value"));
        var invalidValue = new SharpLinkMetadata(
            new KeyValuePair<string, string>("tenant", "value\uDC00"));
        var keyFailure = CaptureException(() => ProtocolV2PayloadCodec.GetMetadataPayloadLength(invalidKey));
        var valueFailure = CaptureException(() => ProtocolV2PayloadCodec.GetMetadataPayloadLength(invalidValue));

        Ensure(keyFailure is ArgumentException, "invalid Unicode metadata key");
        Ensure(valueFailure is ArgumentException, "invalid Unicode metadata value");
    }

    private static Exception? CaptureException(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
