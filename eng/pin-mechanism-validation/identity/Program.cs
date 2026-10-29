using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;

// Metadata-only: never load the target assembly or execute module initializers.
try
{
    if (args.Length != 1) throw new InvalidDataException();
    using var file = File.OpenRead(args[0]);
    using var pe = new PEReader(file);
    var metadata = pe.GetMetadataReader();
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
    {
        var attribute = metadata.GetCustomAttribute(handle);
        if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
        var constructor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
        if (constructor.Parent.Kind != HandleKind.TypeReference) continue;
        var type = metadata.GetTypeReference((TypeReferenceHandle)constructor.Parent);
        if (metadata.GetString(type.Namespace) != "System.Reflection" ||
            metadata.GetString(type.Name) != "AssemblyMetadataAttribute") continue;
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
    Console.WriteLine(JsonSerializer.Serialize(values));
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine("Metadata inspection failed: " + exception.GetType().Name);
    return 1;
}
