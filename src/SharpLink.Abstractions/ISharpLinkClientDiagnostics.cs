namespace SharpLink.Abstractions;

public interface ISharpLinkClientDiagnostics
{
    Exception? LastConnectionException { get; }
}
