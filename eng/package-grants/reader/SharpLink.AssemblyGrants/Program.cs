using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

if (args.Length == 0 || args.Any(static arg => arg.StartsWith("-", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("Usage: SharpLink.AssemblyGrants <assembly-file> [<assembly-file> ...]");
    return 2;
}

try
{
    // Materialize every result before writing anything: a malformed later input
    // must not leave an apparently successful partial JSON inventory.
    var assemblies = args.Select(Inventory.Read).ToArray();
    Console.WriteLine(JsonSerializer.Serialize(new { SchemaVersion = 1, Assemblies = assemblies },
        new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception error) when (error is IOException or UnauthorizedAccessException or
    BadImageFormatException or ArgumentException or InvalidOperationException or OverflowException)
{
    Console.Error.WriteLine($"Assembly grants inventory failed: {error.Message}");
    return 1;
}

internal static class Inventory
{
    private const string Ivt = "System.Runtime.CompilerServices.InternalsVisibleToAttribute";
    private const string Access = "System.Runtime.CompilerServices.IgnoresAccessChecksToAttribute";
    private const string Info = "System.Reflection.AssemblyInformationalVersionAttribute";
    private const string Reference = "System.Runtime.CompilerServices.ReferenceAssemblyAttribute";

    public static object Read(string path)
    {
        // Hash and inspect the same immutable byte snapshot, never load an assembly.
        var bytes = File.ReadAllBytes(path);
        using var pe = new PEReader(new MemoryStream(bytes, writable: false));
        if (!pe.HasMetadata)
            throw new BadImageFormatException($"{Path.GetFileName(path)} has no managed metadata.");
        var reader = pe.GetMetadataReader();
        if (!reader.IsAssembly)
            throw new BadImageFormatException($"{Path.GetFileName(path)} is not an assembly.");
        var assembly = reader.GetAssemblyDefinition();
        var identity = Identity(reader, assembly.Name, assembly.Version, assembly.Culture,
            assembly.PublicKey, assembly.Flags);
        var ivt = new List<string>();
        var access = new List<string>();
        var informationalVersions = new List<string>();
        var attributes = new List<object>();
        var reference = false;
        foreach (var handle in assembly.GetCustomAttributes())
        {
            var attribute = reader.GetCustomAttribute(handle);
            var constructor = Constructor(reader, attribute.Constructor);
            if (constructor.TypeName is not (Ivt or Access or Info or Reference))
                continue;
            var expectsString = constructor.TypeName != Reference ||
                reader.GetBlobBytes(constructor.Signature).AsSpan().SequenceEqual(new byte[] { 0x20, 0x01, 0x01, 0x0e });
            ValidateSignature(reader, constructor.Signature, expectsString);
            var blob = reader.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1)
                throw new BadImageFormatException($"Invalid attribute prolog: {constructor.TypeName}.");
            string? value = expectsString ? ReadString(ref blob) : null;
            if (expectsString && value is null && constructor.TypeName != Reference)
                throw new BadImageFormatException($"Null attribute string: {constructor.TypeName}.");
            bool? allInternalsVisible = null;
            var namedCount = blob.ReadUInt16();
            for (var index = 0; index < namedCount; index++)
            {
                // The sole writable named property in these well-known attributes.
                // Reject unfamiliar payloads rather than silently skip bytes.
                if (constructor.TypeName != Ivt || allInternalsVisible is not null ||
                    blob.ReadByte() != 0x54 || blob.ReadByte() != 0x02 ||
                    ReadString(ref blob) != "AllInternalsVisible")
                    throw new BadImageFormatException($"Unsupported named attribute argument: {constructor.TypeName}.");
                var boolean = blob.ReadByte();
                if (boolean > 1)
                    throw new BadImageFormatException("Invalid AllInternalsVisible boolean.");
                allInternalsVisible = boolean != 0;
            }
            if (blob.RemainingBytes != 0)
                throw new BadImageFormatException($"Trailing attribute payload: {constructor.TypeName}.");
            attributes.Add(new { TypeName = constructor.TypeName, ConstructorKind = attribute.Constructor.Kind.ToString(),
                Value = value, AllInternalsVisible = allInternalsVisible });
            switch (constructor.TypeName)
            {
                case Ivt: ivt.Add(value!); break;
                case Access: access.Add(value!); break;
                case Info: informationalVersions.Add(value!); break;
                case Reference: reference = true; break;
            }
        }
        if (informationalVersions.Count > 1)
            throw new BadImageFormatException("Duplicate AssemblyInformationalVersion attributes.");

        var references = reader.AssemblyReferences.Select(handle =>
        {
            var item = reader.GetAssemblyReference(handle);
            return Identity(reader, item.Name, item.Version, item.Culture, item.PublicKeyOrToken, item.Flags);
        }).ToArray();
        var forwarders = new List<object>();
        foreach (var handle in reader.ExportedTypes)
        {
            var exported = reader.GetExportedType(handle);
            var isForwarder = exported.IsForwarder;
            var name = reader.GetString(exported.Name);
            var ns = reader.GetString(exported.Namespace);
            var scope = exported.Implementation;
            var visited = new HashSet<ExportedTypeHandle> { handle };
            while (scope.Kind == HandleKind.ExportedType)
            {
                var parent = (ExportedTypeHandle)scope;
                if (!visited.Add(parent))
                    throw new BadImageFormatException("Cycle in ExportedType forwarding scopes.");
                var containing = reader.GetExportedType(parent);
                isForwarder |= containing.IsForwarder;
                name = reader.GetString(containing.Name) + "+" + name;
                var containingNamespace = reader.GetString(containing.Namespace);
                if (containingNamespace.Length != 0)
                    ns = containingNamespace;
                scope = containing.Implementation;
            }
            // Nested ExportedType rows commonly inherit forwarding from their
            // containing row instead of repeating the Forwarder flag.
            if (!isForwarder)
                continue;
            if (scope.Kind != HandleKind.AssemblyReference || scope.IsNil)
                throw new BadImageFormatException("Forwarded type does not target an AssemblyRef.");
            var target = reader.GetAssemblyReference((AssemblyReferenceHandle)scope);
            forwarders.Add(new { TypeName = ns.Length == 0 ? name : ns + "." + name,
                Target = Identity(reader, target.Name, target.Version, target.Culture, target.PublicKeyOrToken, target.Flags) });
        }
        return new
        {
            FileName = Path.GetFileName(path), Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)),
            Identity = identity, ModuleVersionId = reader.GetGuid(reader.GetModuleDefinition().Mvid).ToString(),
            InformationalVersion = informationalVersions.SingleOrDefault(),
            InternalsVisibleTo = ivt, IgnoresAccessChecksTo = access, IsReferenceAssembly = reference,
            AssemblyReferences = references, TypeForwarders = forwarders, RecognizedAttributes = attributes,
        };
    }

    private static (string TypeName, BlobHandle Signature) Constructor(MetadataReader reader, EntityHandle handle)
    {
        EntityHandle declaringType;
        StringHandle name;
        BlobHandle signature;
        switch (handle.Kind)
        {
            case HandleKind.MemberReference:
                var member = reader.GetMemberReference((MemberReferenceHandle)handle);
                declaringType = member.Parent; name = member.Name; signature = member.Signature;
                break;
            case HandleKind.MethodDefinition:
                var method = reader.GetMethodDefinition((MethodDefinitionHandle)handle);
                declaringType = method.GetDeclaringType(); name = method.Name; signature = method.Signature;
                break;
            default:
                throw new BadImageFormatException("Invalid custom attribute constructor handle.");
        }
        if (reader.GetString(name) != ".ctor")
            throw new BadImageFormatException("Custom attribute constructor is not .ctor.");
        // Nested and generic attributes cannot be these top-level well-known types.
        string ns, typeName;
        if (declaringType.Kind == HandleKind.TypeReference)
        {
            var type = reader.GetTypeReference((TypeReferenceHandle)declaringType);
            if (type.ResolutionScope.Kind == HandleKind.TypeReference)
                return ("", signature);
            ns = reader.GetString(type.Namespace); typeName = reader.GetString(type.Name);
        }
        else if (declaringType.Kind == HandleKind.TypeDefinition)
        {
            var type = reader.GetTypeDefinition((TypeDefinitionHandle)declaringType);
            if (type.IsNested)
                return ("", signature);
            ns = reader.GetString(type.Namespace); typeName = reader.GetString(type.Name);
        }
        else if (declaringType.Kind == HandleKind.TypeSpecification)
            return ("", signature);
        else
            throw new BadImageFormatException("Invalid custom attribute declaring type.");
        return (ns.Length == 0 ? typeName : ns + "." + typeName, signature);
    }

    private static void ValidateSignature(MetadataReader reader, BlobHandle signature, bool expectsString)
    {
        // DEFAULT | HASTHIS, parameter count, VOID, [STRING]. No generic,
        // vararg, static, optional or trailing signature data is permitted.
        ReadOnlySpan<byte> expected = expectsString ? [0x20, 0x01, 0x01, 0x0e] : [0x20, 0x00, 0x01];
        if (!reader.GetBlobBytes(signature).AsSpan().SequenceEqual(expected))
            throw new BadImageFormatException("Unexpected well-known attribute constructor signature.");
    }

    private static string? ReadString(ref BlobReader blob)
    {
        if (blob.RemainingBytes == 0)
            throw new BadImageFormatException("Missing serialized attribute string.");
        var start = blob.Offset;
        if (blob.ReadByte() == 0xff)
            return null;
        blob.Offset = start;
        var length = blob.ReadCompressedInteger();
        if (length < 0 || length > blob.RemainingBytes)
            throw new BadImageFormatException("Truncated serialized attribute string.");
        // BlobReader's convenience string decoder replaces invalid UTF-8. Reject
        // it here so malformed bytes cannot be normalized into a valid grant.
        return new UTF8Encoding(false, true).GetString(blob.ReadBytes(length));
    }

    private static object Identity(MetadataReader reader, StringHandle name, Version version,
        StringHandle culture, BlobHandle key, AssemblyFlags flags)
    {
        var keyBytes = reader.GetBlobBytes(key);
        var identity = new AssemblyName
        {
            Name = reader.GetString(name), Version = version, CultureName = reader.GetString(culture),
            Flags = (AssemblyNameFlags)((int)flags & 0x101),
            ContentType = (flags & AssemblyFlags.WindowsRuntime) != 0 ? AssemblyContentType.WindowsRuntime : AssemblyContentType.Default,
        };
        if ((flags & AssemblyFlags.PublicKey) != 0)
            identity.SetPublicKey(keyBytes);
        else
        {
            if (keyBytes.Length is not (0 or 8))
                throw new BadImageFormatException("Invalid assembly public-key token length.");
            identity.SetPublicKeyToken(keyBytes);
        }
        return new { identity.Name, Version = version.ToString(), Culture = identity.CultureName,
            identity.FullName, PublicKeyOrToken = Convert.ToHexStringLower(keyBytes),
            HasFullPublicKey = (flags & AssemblyFlags.PublicKey) != 0, Flags = (int)flags };
    }
}
