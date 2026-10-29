using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Metadata-only: never load the target assembly or execute module initializers.
try
{
    var assemblyMode = args.Length == 2 && args[0] == "--assembly";
    if (args.Length != 1 && !assemblyMode) throw new InvalidDataException();
    using var file = File.OpenRead(args[assemblyMode ? 1 : 0]);
    using var pe = new PEReader(file);
    var metadata = pe.GetMetadataReader();
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    string? informationalVersion = null;
    foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (metadata.GetString(type.Namespace) != "System.Reflection") continue;
        if (assemblyMode)
        {
            if (metadata.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
            if (informationalVersion is not null || metadata.GetString(constructor.Name) != ".ctor" ||
                !metadata.GetBlobBytes(constructor.Signature).AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x0e }))
                throw new InvalidDataException();
            var version = metadata.GetBlobReader(attribute.Value);
            if (version.ReadUInt16() != 1) throw new InvalidDataException();
            informationalVersion = version.ReadSerializedString();
            if (string.IsNullOrEmpty(informationalVersion) || version.ReadUInt16() != 0 || version.RemainingBytes != 0)
                throw new InvalidDataException();
            continue;
        }
        if (metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
        if (metadata.GetString(constructor.Name) != ".ctor" ||
            !metadata.GetBlobBytes(constructor.Signature).AsSpan().SequenceEqual(new byte[] { 0x20, 2, 1, 0x0e, 0x0e }))
            throw new InvalidDataException();
        var value = metadata.GetBlobReader(attribute.Value);
        if (value.ReadUInt16() != 1) throw new InvalidDataException();
        var key = value.ReadSerializedString() ?? throw new InvalidDataException();
        var text = value.ReadSerializedString() ?? throw new InvalidDataException();
        if (value.ReadUInt16() != 0 || value.RemainingBytes != 0 || !values.TryAdd(key, text))
            throw new InvalidDataException();
    }
    if (assemblyMode)
    {
        var assemblyName = metadata.GetString(metadata.GetAssemblyDefinition().Name);
        var mvid = metadata.GetGuid(metadata.GetModuleDefinition().Mvid);
        if (string.IsNullOrEmpty(assemblyName) || mvid == Guid.Empty || informationalVersion is null)
            throw new InvalidDataException();
        Console.WriteLine(JsonSerializer.Serialize(new { assemblyName, mvid, informationalVersion }));
    }
    else Console.WriteLine(JsonSerializer.Serialize(values));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Metadata inspection failed: " + exception.GetType().Name);
    return 1;
}
