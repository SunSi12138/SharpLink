using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length < 2) throw new ArgumentException("output assembly...");
var assemblies = new List<object>();
foreach (string path in args.Skip(1))
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var reader = pe.GetMetadataReader();
    var methods = new List<object>();
    foreach (var th in reader.TypeDefinitions)
    {
        var type = reader.GetTypeDefinition(th);
        string typeName = TypeName(th);
        foreach (var mh in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(mh);
            string name = reader.GetString(method.Name);
            var attributes = new List<string>();
            string? stateMachineType = null, builderType = null;
            foreach (var ah in method.GetCustomAttributes())
            {
                var attribute = reader.GetCustomAttribute(ah);
                EntityHandle owner = attribute.Constructor.Kind == HandleKind.MemberReference
                    ? reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor).Parent
                    : reader.GetMethodDefinition((MethodDefinitionHandle)attribute.Constructor).GetDeclaringType();
                string attributeName = TypeName(owner);
                attributes.Add(attributeName);
                if (attributeName.EndsWith(".AsyncStateMachineAttribute", StringComparison.Ordinal) || attributeName.EndsWith(".AsyncMethodBuilderAttribute", StringComparison.Ordinal))
                {
                    var blob = reader.GetBlobReader(attribute.Value);
                    if (blob.ReadUInt16() != 1) throw new InvalidOperationException("Unexpected custom-attribute prolog");
                    string? value = blob.ReadSerializedString();
                    if (attributeName.EndsWith(".AsyncStateMachineAttribute", StringComparison.Ordinal)) stateMachineType = value;
                    else builderType = value;
                }
            }
            bool hasMoveNext = stateMachineType is not null && reader.TypeDefinitions.Any(handle =>
            {
                string nestedName = TypeName(handle);
                return stateMachineType.StartsWith(nestedName + ",", StringComparison.Ordinal) || stateMachineType == nestedName;
            });
            if (hasMoveNext)
            {
                var handle = reader.TypeDefinitions.Single(handle => stateMachineType!.StartsWith(TypeName(handle) + ",", StringComparison.Ordinal) || stateMachineType == TypeName(handle));
                hasMoveNext = reader.GetTypeDefinition(handle).GetMethods().Any(handle => reader.GetString(reader.GetMethodDefinition(handle).Name) == "MoveNext");
            }
            var semantic = method.DecodeSignature(new SignatureNames(), (object?)null);
            string semanticSignature = semantic.ReturnType + " (" + string.Join(",", semantic.ParameterTypes) + ")";
            methods.Add(new { type = typeName, name, genericArity = method.GetGenericParameters().Count, semanticSignature,
                signature = Convert.ToHexString(reader.GetBlobBytes(method.Signature)),
                implementationFlags = (int)method.ImplAttributes, runtimeAsync = ((int)method.ImplAttributes & 0x2000) != 0,
                stateMachineType, hasMoveNext, builderType, attributes });
        }
    }
    var definition = reader.GetAssemblyDefinition();
    var identity = new { name = reader.GetString(definition.Name), version = definition.Version.ToString(),
        culture = reader.GetString(definition.Culture), publicKey = Convert.ToHexString(reader.GetBlobBytes(definition.PublicKey)) };
    assemblies.Add(new { path, identity, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), methods });
    string TypeName(EntityHandle handle)
    {
        if (handle.Kind == HandleKind.TypeDefinition)
        {
            var value = reader.GetTypeDefinition((TypeDefinitionHandle)handle);
            string name = reader.GetString(value.Name);
            var declaring = value.GetDeclaringType();
            if (!declaring.IsNil) return TypeName(declaring) + "+" + name;
            string ns = reader.GetString(value.Namespace);
            return string.IsNullOrEmpty(ns) ? name : ns + "." + name;
        }
        if (handle.Kind == HandleKind.TypeReference)
        {
            var value = reader.GetTypeReference((TypeReferenceHandle)handle);
            return reader.GetString(value.Namespace) + "." + reader.GetString(value.Name);
        }
        return "<" + handle.Kind + ">";
    }
}
string corelib = typeof(object).Assembly.Location;
File.WriteAllText(args[0], JsonSerializer.Serialize(new {
    schemaVersion = 1, processId = Environment.ProcessId, runtime = Environment.Version.ToString(),
    framework = RuntimeInformation.FrameworkDescription, corelibPath = corelib,
    corelibSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(corelib))).ToLowerInvariant(), assemblies
}, new JsonSerializerOptions { WriteIndented = true }));

// Compare semantic types, not token-indexed signature blobs that legitimately move between lowerings.
internal sealed class SignatureNames : ISignatureTypeProvider<string, object?>
{
    public string GetArrayType(string elementType, ArrayShape shape) => elementType + "[rank=" + shape.Rank + "]";
    public string GetByReferenceType(string elementType) => elementType + "&";
    public string GetFunctionPointerType(MethodSignature<string> signature) => "fn(" + string.Join(",", signature.ParameterTypes) + ")->" + signature.ReturnType;
    public string GetGenericInstantiation(string genericType, ImmutableArray<string> typeArguments) => genericType + "<" + string.Join(",", typeArguments) + ">";
    public string GetGenericMethodParameter(object? genericContext, int index) => "!!" + index;
    public string GetGenericTypeParameter(object? genericContext, int index) => "!" + index;
    public string GetModifiedType(string modifier, string unmodifiedType, bool isRequired) => unmodifiedType + (isRequired ? " modreq(" : " modopt(") + modifier + ")";
    public string GetPinnedType(string elementType) => elementType + " pinned";
    public string GetPointerType(string elementType) => elementType + "*";
    public string GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode.ToString();
    public string GetSZArrayType(string elementType) => elementType + "[]";
    public string GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeDefinition(handle);
        string name = reader.GetString(type.Name);
        var declaring = type.GetDeclaringType();
        return !declaring.IsNil ? GetTypeFromDefinition(reader, declaring, rawTypeKind) + "+" + name
            : reader.GetString(type.Namespace) + "." + name;
    }
    public string GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
    {
        var type = reader.GetTypeReference(handle);
        string name = reader.GetString(type.Name);
        return type.ResolutionScope.Kind == HandleKind.TypeReference
            ? GetTypeFromReference(reader, (TypeReferenceHandle)type.ResolutionScope, rawTypeKind) + "+" + name
            : reader.GetString(type.Namespace) + "." + name;
    }
    public string GetTypeFromSpecification(MetadataReader reader, object? genericContext, TypeSpecificationHandle handle, byte rawTypeKind)
        => reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);
}
