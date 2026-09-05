namespace SharpLink.AotExternalPayloads;

public sealed class ExternalAotPayload
{
    public int Id { get; set; }
    public List<ExternalAotChild> Children { get; set; } = [];
    public Dictionary<string, ExternalAotChild> ByName { get; set; } = new();
}

public sealed class ExternalAotChild
{
    public string Name { get; set; } = string.Empty;
}
