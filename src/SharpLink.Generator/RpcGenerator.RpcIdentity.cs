namespace SharpLink.Generator;

public partial class RpcGenerator
{
    private sealed record RpcAssemblyIdentityModel(
        RpcHashValue AssemblyHash,
        ImmutableArray<RpcContractIdentityModel> Contracts);

    private sealed record RpcContractIdentityModel(
        long ContractId,
        RpcHashValue ContractHash,
        ImmutableArray<RpcMethodIdentityModel> Methods);

    private sealed record RpcMethodIdentityModel(
        long MethodId,
        RpcHashValue MethodHash);

    private static RpcAssemblyIdentityModel BuildRpcAssemblyIdentity(
        RpcInterfaceModel[] contracts,
        ImmutableArray<GeneratedCodecHashModel> codecHashes)
    {
        var codecHashByType = codecHashes.ToDictionary(
            static item => item.TypeName,
            static item => new RpcHashValue(item.High, item.Low),
            StringComparer.Ordinal);
        var contractIdentities = contracts
            .OrderBy(static contract => contract.Hash)
            .Select(contract => BuildContractIdentity(contract, codecHashByType))
            .ToImmutableArray();
        var assemblyParts = new List<string>
        {
            "rpc-assembly/v1",
            contractIdentities.Length.ToString(InvariantCulture)
        };
        foreach (var contract in contractIdentities)
        {
            assemblyParts.Add(contract.ContractId.ToString(InvariantCulture));
            assemblyParts.Add(contract.ContractHash.ToHex());
        }

        return new RpcAssemblyIdentityModel(
            Hashing.GetSemanticHash(assemblyParts.ToArray()),
            contractIdentities);
    }

    private static RpcContractIdentityModel BuildContractIdentity(
        RpcInterfaceModel contract,
        IReadOnlyDictionary<string, RpcHashValue> codecHashes)
    {
        var methods = contract.Methods
            .OrderBy(static method => method.Hash)
            .Select(method => new RpcMethodIdentityModel(
                method.Hash,
                BuildMethodHash(method, codecHashes)))
            .ToImmutableArray();
        var parts = new List<string>
        {
            "contract/v1",
            contract.Hash.ToString(InvariantCulture),
            methods.Length.ToString(InvariantCulture)
        };
        foreach (var method in methods)
        {
            parts.Add(method.MethodId.ToString(InvariantCulture));
            parts.Add(method.MethodHash.ToHex());
        }

        return new RpcContractIdentityModel(
            contract.Hash,
            Hashing.GetSemanticHash(parts.ToArray()),
            methods);
    }

    private static RpcHashValue BuildMethodHash(
        RpcMethodModel method,
        IReadOnlyDictionary<string, RpcHashValue> codecHashes)
    {
        var payloadParameters = method.Parameters
            .Where(static parameter => !parameter.IsCancellationToken)
            .ToArray();
        var parts = new List<string>
        {
            "method/v1",
            method.Hash.ToString(InvariantCulture),
            GetMethodKind(method),
            method.HasCancellationToken ? "cancellable" : "non-cancellable",
            method.IsIdempotent ? "idempotent" : "non-idempotent",
            method.HasTimeoutAttribute ? "timeout" : "no-timeout",
            method.TimeoutSeconds?.ToString("R", InvariantCulture) ?? string.Empty,
            payloadParameters.Length.ToString(InvariantCulture)
        };
        for (var index = 0; index < payloadParameters.Length; index++)
        {
            var parameter = payloadParameters[index];
            var payloadType = parameter.IsStream
                ? parameter.StreamItemType ?? throw new InvalidOperationException(
                    $"Streaming RPC parameter '{parameter.Name}' has no item type.")
                : parameter.Type;
            parts.Add(index.ToString(InvariantCulture));
            parts.Add(parameter.IsStream ? "stream" : "unary");
            parts.Add(parameter.PayloadNullable ? "nullable" : "non-nullable");
            parts.Add(GetRequiredCodecHash(payloadType, codecHashes).ToHex());
        }

        if (method.IsVoid)
        {
            parts.Add("response:void");
        }
        else
        {
            var responseType = method.IsStreamReturn
                ? method.StreamItemType ?? throw new InvalidOperationException(
                    $"Streaming RPC method '{method.Name}' has no item type.")
                : method.GenericArgumentType ?? throw new InvalidOperationException(
                    $"RPC method '{method.Name}' has no response payload type.");
            parts.Add(method.IsStreamReturn ? "response:stream" : "response:unary");
            parts.Add(method.ResponseNullable ? "nullable" : "non-nullable");
            parts.Add(GetRequiredCodecHash(responseType, codecHashes).ToHex());
        }

        return Hashing.GetSemanticHash(parts.ToArray());
    }

    private static RpcHashValue GetRequiredCodecHash(
        string typeName,
        IReadOnlyDictionary<string, RpcHashValue> codecHashes)
    {
        if (codecHashes.TryGetValue(typeName, out var hash))
            return hash;
        throw new InvalidOperationException(
            $"Final RPC Codec graph is missing deterministic identity for '{typeName}'.");
    }
}
