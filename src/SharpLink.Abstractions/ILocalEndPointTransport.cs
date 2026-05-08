using System.Net;

namespace SharpLink.Abstractions;

public interface ILocalEndPointTransport
{
    EndPoint? LocalEndPoint { get; }
}
