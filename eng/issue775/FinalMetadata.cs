// Diagnostic reader only. Does not load or execute the inspected assemblies.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

internal sealed class Signatures : ISignatureTypeProvider<string, object?>
{
    internal readonly MetadataReader Reader;
    internal int Unresolved;
    internal Signatures(MetadataReader reader) => Reader = reader;
    internal static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    internal string Blob(BlobHandle h) => Hash(Reader.GetBlobBytes(h));
    internal static string Method(MethodSignature<string> s) => $"{s.Header.RawValue}:{s.GenericParameterCount}:{s.RequiredParameterCount}:{s.ReturnType}({string.Join(",", s.ParameterTypes)})";
    internal string TypeName(TypeDefinitionHandle h)
    {
        var t = Reader.GetTypeDefinition(h);
        return t.GetDeclaringType().IsNil ? Reader.GetString(t.Namespace) + "." + Reader.GetString(t.Name)
            : TypeName(t.GetDeclaringType()) + "+" + Reader.GetString(t.Name);
    }
    internal string Entity(EntityHandle h)
    {
        if (h.IsNil) return "nil";
        switch (h.Kind)
        {
            case HandleKind.TypeDefinition: return TypeName((TypeDefinitionHandle)h);
            case HandleKind.TypeReference:
                var t = Reader.GetTypeReference((TypeReferenceHandle)h);
                return Entity(t.ResolutionScope) + "::" + Reader.GetString(t.Namespace) + "." + Reader.GetString(t.Name);
            case HandleKind.TypeSpecification: return Reader.GetTypeSpecification((TypeSpecificationHandle)h).DecodeSignature(this, null);
            case HandleKind.MethodDefinition:
                var m = Reader.GetMethodDefinition((MethodDefinitionHandle)h);
                return TypeName(m.GetDeclaringType()) + "::" + Reader.GetString(m.Name) + ":" + Method(m.DecodeSignature(this, null));
            case HandleKind.MemberReference:
                var r = Reader.GetMemberReference((MemberReferenceHandle)h);
                return Entity(r.Parent) + "::" + Reader.GetString(r.Name) + ":" + (r.GetKind() == MemberReferenceKind.Method ? Method(r.DecodeMethodSignature(this, null)) : r.DecodeFieldSignature(this, null));
            case HandleKind.FieldDefinition:
                var f = Reader.GetFieldDefinition((FieldDefinitionHandle)h);
                return TypeName(f.GetDeclaringType()) + "::" + Reader.GetString(f.Name) + ":" + f.DecodeSignature(this, null);
            case HandleKind.MethodSpecification:
                var s = Reader.GetMethodSpecification((MethodSpecificationHandle)h);
                return Entity(s.Method) + "<" + string.Join(",", s.DecodeSignature(this, null)) + ">";
            case HandleKind.AssemblyReference:
                var a = Reader.GetAssemblyReference((AssemblyReferenceHandle)h);
                return Reader.GetString(a.Name) + "," + a.Version + "," + Blob(a.PublicKeyOrToken);
            case HandleKind.ModuleDefinition: return Reader.GetString(Reader.GetModuleDefinition().Name);
            case HandleKind.ModuleReference: return Reader.GetString(Reader.GetModuleReference((ModuleReferenceHandle)h).Name);
            case HandleKind.StandaloneSignature:
                var sig = Reader.GetStandaloneSignature((StandaloneSignatureHandle)h);
                return sig.GetKind() == StandaloneSignatureKind.Method ? Method(sig.DecodeMethodSignature(this, null)) : string.Join(",", sig.DecodeLocalSignature(this, null));
            default: Unresolved++; return $"unresolved:{h.Kind}:{MetadataTokens.GetToken(h):X8}";
        }
    }
    internal string Token(int value) => (value & unchecked((int)0xFF000000)) == 0x70000000
        ? JsonSerializer.Serialize(Reader.GetUserString(MetadataTokens.UserStringHandle(value & 0x00FFFFFF)))
        : Entity(MetadataTokens.EntityHandle(value));
    public string GetArrayType(string e, ArrayShape s) => e + "[rank=" + s.Rank + ";sizes=" + string.Join(",", s.Sizes) + ";lower=" + string.Join(",", s.LowerBounds) + "]";
    public string GetByReferenceType(string e) => e + "&";
    public string GetFunctionPointerType(MethodSignature<string> s) => "fn:" + Method(s);
    public string GetGenericInstantiation(string e, ImmutableArray<string> a) => e + "<" + string.Join(",", a) + ">";
    public string GetGenericMethodParameter(object? c, int i) => "!!" + i;
    public string GetGenericTypeParameter(object? c, int i) => "!" + i;
    public string GetModifiedType(string m, string e, bool required) => e + (required ? " modreq(" : " modopt(") + m + ")";
    public string GetPinnedType(string e) => e + " pinned";
    public string GetPointerType(string e) => e + "*";
    public string GetPrimitiveType(PrimitiveTypeCode c) => c.ToString();
    public string GetSZArrayType(string e) => e + "[]";
    public string GetTypeFromDefinition(MetadataReader r, TypeDefinitionHandle h, byte k) => Entity(h);
    public string GetTypeFromReference(MetadataReader r, TypeReferenceHandle h, byte k) => Entity(h);
    public string GetTypeFromSpecification(MetadataReader r, object? c, TypeSpecificationHandle h, byte k) => Entity(h);
}

internal static class Program
{
    private static readonly Dictionary<ushort, OpCode> Codes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(f => f.FieldType == typeof(OpCode)).Select(f => (OpCode)f.GetValue(null)!).GroupBy(o => unchecked((ushort)o.Value)).ToDictionary(g => g.Key, g => g.First());
    private static string Normalize(byte[] bytes, Signatures s)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        var text = new StringBuilder();
        while (reader.BaseStream.Position < bytes.Length)
        {
            var offset = reader.BaseStream.Position;
            ushort key = reader.ReadByte();
            if (key == 0xFE) key = (ushort)(0xFE00 | reader.ReadByte());
            if (!Codes.TryGetValue(key, out var op)) throw new BadImageFormatException("Unknown IL opcode");
            string operand = op.OperandType switch
            {
                OperandType.InlineNone => "",
                OperandType.ShortInlineI or OperandType.ShortInlineBrTarget => reader.ReadSByte().ToString(),
                OperandType.ShortInlineVar => reader.ReadByte().ToString(),
                OperandType.InlineVar => reader.ReadUInt16().ToString(),
                OperandType.InlineI or OperandType.InlineBrTarget => reader.ReadInt32().ToString(),
                OperandType.InlineI8 => reader.ReadInt64().ToString(),
                OperandType.ShortInlineR => Convert.ToHexString(reader.ReadBytes(4)),
                OperandType.InlineR => Convert.ToHexString(reader.ReadBytes(8)),
                OperandType.InlineField or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString or OperandType.InlineTok or OperandType.InlineType => s.Token(reader.ReadInt32()),
                OperandType.InlineSwitch => string.Join(",", Enumerable.Range(0, reader.ReadInt32()).Select(_ => reader.ReadInt32())),
                _ => throw new BadImageFormatException("Unsupported IL operand " + op.OperandType)
            };
            text.Append(offset).Append(':').Append(op.Name).Append(' ').Append(operand).Append('\n');
        }
        return text.ToString();
    }
    private static object Inspect(string path)
    {
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();
        var s = new Signatures(md);
        var methods = new List<object>();
        foreach (var h in md.MethodDefinitions)
        {
            var m = md.GetMethodDefinition(h);
            string? raw = null, normalized = null;
            if (m.RelativeVirtualAddress != 0)
            {
                var b = pe.GetMethodBody(m.RelativeVirtualAddress);
                var il = b.GetILBytes() ?? Array.Empty<byte>();
                raw = Signatures.Hash(il);
                var body = new { b.MaxStack, b.LocalVariablesInitialized,
                    locals = b.LocalSignature.IsNil ? "" : s.Entity(b.LocalSignature),
                    exceptions = b.ExceptionRegions.Select(e => new { kind = e.Kind.ToString(), e.TryOffset, e.TryLength, e.HandlerOffset, e.HandlerLength, e.FilterOffset, type = e.Kind == ExceptionRegionKind.Catch ? s.Entity(e.CatchType) : "" }).ToArray(),
                    instructions = Normalize(il, s) };
                normalized = JsonSerializer.Serialize(body);
            }
            methods.Add(new { token = MetadataTokens.GetToken(h), name = s.Entity(h), attributes = (int)m.Attributes,
                implementation = (int)m.ImplAttributes, rawIlSha256 = raw,
                normalizedBodySha256 = normalized is null ? null : Signatures.Hash(Encoding.UTF8.GetBytes(normalized)), normalizedBody = normalized });
        }
        var types = md.TypeDefinitions.Select(h => {
            var t = md.GetTypeDefinition(h); var layout = t.GetLayout();
            return new { name = s.Entity(h), attributes = (int)t.Attributes, parent = s.Entity(t.BaseType), layout.PackingSize, layout.Size,
                interfaces = t.GetInterfaceImplementations().Select(i => s.Entity(md.GetInterfaceImplementation(i).Interface)).ToArray(),
                fields = t.GetFields().Select(fh => { var f = md.GetFieldDefinition(fh); return new { name = s.Entity(fh), attributes = (int)f.Attributes, offset = f.GetOffset() }; }).ToArray() };
        }).ToArray();
        var debug = pe.ReadDebugDirectory().Select(e => {
            string? pdbPath = null, guid = null;
            if (e.Type == DebugDirectoryEntryType.CodeView) { var cv = pe.ReadCodeViewDebugDirectoryData(e); pdbPath = cv.Path; guid = cv.Guid.ToString(); }
            return new { type = e.Type.ToString(), e.Stamp, e.DataSize, pdbPath, guid };
        }).ToArray();
        var attributes = md.CustomAttributes.Select(h => { var a = md.GetCustomAttribute(h); return new { parentKind = a.Parent.Kind.ToString(), parentToken = MetadataTokens.GetToken(a.Parent), constructor = s.Entity(a.Constructor), value = Convert.ToHexString(md.GetBlobBytes(a.Value)) }; }).ToArray();
        return new { file = Path.GetFileName(path), sha256 = Signatures.Hash(File.ReadAllBytes(path)),
            mvid = md.GetGuid(md.GetModuleDefinition().Mvid), pe.PEHeaders.CoffHeader.TimeDateStamp,
            metadataSha256 = Signatures.Hash(pe.GetMetadata().GetContent().ToArray()),
            sections = pe.PEHeaders.SectionHeaders.Select(sec => new { sec.Name, sha256 = Signatures.Hash(pe.GetSectionData(sec.VirtualAddress).GetContent().ToArray()) }).ToArray(),
            debug, types, methods, attributes, unresolvedTokens = s.Unresolved,
            scope = "Decoded method-body comparison is diagnostic, not proof of complete runtime behavior or performance equivalence." };
    }
    private static void Main(string[] args)
    {
        if (args.Length != 2) throw new ArgumentException("input-dll-directory output-json");
        var result = Directory.GetFiles(args[0], "SharpLink*.dll").OrderBy(p => p, StringComparer.Ordinal).Select(Inspect).ToArray();
        if (result.Length == 0) throw new InvalidOperationException("No managed SharpLink assemblies");
        File.WriteAllText(args[1], JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
    }
}
