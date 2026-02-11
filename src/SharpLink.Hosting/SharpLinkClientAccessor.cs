namespace SharpLink.Hosting;

internal sealed class SharpLinkClientAccessor : ISharpLinkClientAccessor
{
    public ISharpLinkClient? Client { get; set; }
}
