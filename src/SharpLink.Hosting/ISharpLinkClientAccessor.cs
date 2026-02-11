namespace SharpLink.Hosting;

public interface ISharpLinkClientAccessor
{
    ISharpLinkClient? Client { get; }
}
