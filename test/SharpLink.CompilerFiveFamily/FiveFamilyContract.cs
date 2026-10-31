using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SharpLink.Abstractions;
using SharpLink.Sdk;

namespace SharpLink.CompilerFiveFamily;

// Compiler-only input to the actual SharpLink generator. No fixture assembly is executed.
[RpcContract]
public interface IFiveFamilyContract : IService
{
    ValueTask<int> UnaryAsync(int value, CancellationToken cancellationToken);

    [Oneway]
    ValueTask OneWayAsync(int value, CancellationToken cancellationToken);

    ValueTask<int> ClientStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);

    IAsyncEnumerable<int> ServerStreamingAsync(int value, CancellationToken cancellationToken);

    IAsyncEnumerable<int> DuplexStreamingAsync(IAsyncEnumerable<int> values, CancellationToken cancellationToken);

    ValueTask<int> ClientStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken);

    IAsyncEnumerable<int> DuplexStreamingWithSeedAsync(int seed, IAsyncEnumerable<int> values, CancellationToken cancellationToken);
}
