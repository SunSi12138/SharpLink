using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using SharpLink.Sdk;

namespace SharpLink.Benchmarks;

// Test-only contract. Both A and B compile the same normal generated proxy, dispatcher,
// manifest and DTO codec; no hand-written sized codec is substituted into the runtime.
[RpcContract]
public interface IGrantBenchmarkRpc : IService
{
    [NonCancellable]
    ValueTask<int> ProbeAsync();

    [NonCancellable]
    ValueTask<GrantBenchmarkPayload> EchoAsync(GrantBenchmarkPayload value);

    [NonCancellable]
    ValueTask<long> UploadAsync(IAsyncEnumerable<GrantBenchmarkPayload> values);

    [NonCancellable]
    IAsyncEnumerable<GrantBenchmarkPayload> DownloadAsync(int count, int encodedBytes);

    [NonCancellable]
    IAsyncEnumerable<GrantBenchmarkPayload> DuplexAsync(IAsyncEnumerable<GrantBenchmarkPayload> values);
}

[RpcSerializable]
public sealed class GrantBenchmarkPayload
{
    [RpcMember(1)] public string Text { get; set; } = string.Empty;
    [RpcMember(2)] public ushort Tag { get; set; }

    internal const ushort ExpectedTag = 0x735B;
    internal const int ExpectedEncodingOverhead = 10;

    internal static GrantBenchmarkPayload Create(int targetEncodedBytes)
    {
        // Reference presence + object end = 2; string key/length = 5;
        // ushort field key/value = 3. The generated UTF-16 payload is two bytes per char.
        // ProbeCodec must verify this prediction against actual generated output before timing.
        if (targetEncodedBytes < ExpectedEncodingOverhead || (targetEncodedBytes & 1) != 0)
            throw new ArgumentOutOfRangeException(nameof(targetEncodedBytes),
                "The generated grant DTO requires an even encoded size of at least ten bytes.");
        var characters = new char[(targetEncodedBytes - ExpectedEncodingOverhead) / sizeof(char)];
        for (var index = 0; index < characters.Length; index++)
            characters[index] = (char)('a' + ((index * 17 + 11) % 26));
        return new GrantBenchmarkPayload { Text = new string(characters), Tag = ExpectedTag };
    }
}
