using System.Collections.Generic;
using SharpLink.Sdk;

namespace SharpLink.UnitTests.Abstractions;

public class SharpLinkCallOptionsTests
{
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

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
