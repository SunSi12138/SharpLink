using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ForwarderProbe;

internal static class Program
{
    internal const string SourceCommit = "8a13b12e799bb70a989eacac0c1b4cea22ab80ae";
    internal const string InformationalVersion = "3.0.0+" + SourceCommit;
    internal const string SdkIdentity = "SharpLink.Sdk, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null";
    internal const string AbstractionsIdentity = "SharpLink.Abstractions, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null";
    internal static readonly string[] ContractNames =
    [
        "SharpLink.Sdk.IService",
        "SharpLink.Sdk.IdempotentAttribute",
        "SharpLink.Sdk.NonCancellableAttribute",
        "SharpLink.Sdk.OnewayAttribute",
        "SharpLink.Sdk.RpcCodecAdapterAttribute",
        "SharpLink.Sdk.RpcCodecAdapterRegistrationAttribute",
        "SharpLink.Sdk.RpcCodecRouteAttribute",
        "SharpLink.Sdk.RpcCodecScope",
        "SharpLink.Sdk.RpcContractAttribute",
        "SharpLink.Sdk.RpcIgnoreAttribute",
        "SharpLink.Sdk.RpcMemberAttribute",
        "SharpLink.Sdk.RpcRequiredAttribute",
        "SharpLink.Sdk.RpcSerializableAttribute",
        "SharpLink.Sdk.RpcServiceAttribute",
        "SharpLink.Sdk.RpcUnionCaseAttribute",
        "SharpLink.Sdk.SharpLinkClusterContractAssemblyAttribute",
        "SharpLink.Sdk.SharpLinkMetadata",
        "SharpLink.Sdk.SharpLinkRpcContractsAttribute",
        "SharpLink.Sdk.SharpLinkServiceLifetime",
        "SharpLink.Sdk.TimeoutAttribute"
    ];
    private static readonly HashSet<string> ContractSet = new(ContractNames, StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static int Main(string[] args)
    {
        bool selfTest = args is ["--self-test"];
        try
        {
            object result = selfTest ? SelfTests.Run() : Run(Parse(args));
            Console.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new FailureResult(
                1, selfTest ? "synthetic-self-test" : "packaged-sdk-forwarding",
                false, ErrorCode(error).ToString(), SourceCommit, selfTest ? SelfTests.Stage : null,
                error switch { ArgumentException => "Argument", InvalidOperationException => "InvalidOperation",
                    NotSupportedException => "NotSupported", _ => null }), JsonOptions));
            return 1;
        }
    }

    internal static Inputs Parse(string[] args)
    {
        Require(args.Length == 8, FailureCode.InvalidArguments);
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        foreach (int index in new[] { 0, 2, 4, 6 })
        {
            string key = args[index];
            Require(key is "--sdk" or "--sdk-sha256" or "--abstractions" or "--abstractions-sha256",
                FailureCode.InvalidArguments);
            Require(values.TryAdd(key, args[index + 1]), FailureCode.InvalidArguments);
        }
        Require(values.Count == 4, FailureCode.InvalidArguments);
        string sdk = ValidatePath(values["--sdk"]);
        string abstractions = ValidatePath(values["--abstractions"]);
        Require(!string.Equals(sdk, abstractions, StringComparison.Ordinal), FailureCode.InvalidArguments);
        return new Inputs(sdk, ValidateHash(values["--sdk-sha256"]),
            abstractions, ValidateHash(values["--abstractions-sha256"]));
    }

    private static string ValidatePath(string value)
    {
        Require(Path.IsPathFullyQualified(value) && value.Length <= 4096 &&
            !value.Contains('\0') && string.Equals(Path.GetExtension(value), ".dll", StringComparison.OrdinalIgnoreCase),
            FailureCode.InvalidArguments);
        return Path.GetFullPath(value);
    }

    private static string ValidateHash(string value)
    {
        Require(value.Length == 64 && value.All(Uri.IsHexDigit), FailureCode.InvalidArguments);
        return value.ToLowerInvariant();
    }

    internal static ProbeResult Run(Inputs input)
    {
        using AssemblyImage sdk = AssemblyImage.Read(input.SdkPath, input.SdkSha256, SdkIdentity);
        using AssemblyImage abstractions = AssemblyImage.Read(input.AbstractionsPath, input.AbstractionsSha256, AbstractionsIdentity);
        ValidateMetadata(sdk.Reader, abstractions.Reader);
        RuntimeEvidence runtime = CheckRuntime(sdk, abstractions);
        // Re-read both paths: report failure if either supplied artifact changed during the probe.
        sdk.VerifyUnchanged();
        abstractions.VerifyUnchanged();
        return new ProbeResult(1, "packaged-sdk-forwarding", true, "Pass", SourceCommit,
            "ExplicitSdkQualifiedForwardingOnly", false, false, ContractNames.Length,
            sdk.Evidence(), abstractions.Evidence(), Environment.Version.ToString(), runtime);
    }

    internal static void ValidateMetadata(MetadataReader sdk, MetadataReader abstractions)
    {
        Require(sdk.ExportedTypes.Count == ContractNames.Length, FailureCode.ExportedTypeCountMismatch);
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (ExportedTypeHandle handle in sdk.ExportedTypes)
        {
            ExportedType type = sdk.GetExportedType(handle);
            string name = Qualified(sdk.GetString(type.Namespace), sdk.GetString(type.Name));
            Require(ContractSet.Contains(name), FailureCode.UnknownExportedType);
            Require(names.Add(name), FailureCode.DuplicateExportedType);
            Require(type.IsForwarder && type.Implementation.Kind == HandleKind.AssemblyReference,
                FailureCode.NotAssemblyForwarder);
            AssemblyReference reference = sdk.GetAssemblyReference((AssemblyReferenceHandle)type.Implementation);
            Require(Identity(sdk, reference) == AbstractionsIdentity && reference.Flags == 0,
                FailureCode.WrongDestinationIdentity);
        }
        Require(names.SetEquals(ContractSet), FailureCode.ExportedTypeSetMismatch);
        foreach (TypeDefinitionHandle handle in sdk.TypeDefinitions)
        {
            TypeDefinition type = sdk.GetTypeDefinition(handle);
            Require(!ContractSet.Contains(Qualified(sdk.GetString(type.Namespace), sdk.GetString(type.Name))),
                FailureCode.LocalSdkImpostor);
        }
        HashSet<string> destinations = new(StringComparer.Ordinal);
        foreach (TypeDefinitionHandle handle in abstractions.TypeDefinitions)
        {
            TypeDefinition type = abstractions.GetTypeDefinition(handle);
            string name = Qualified(abstractions.GetString(type.Namespace), abstractions.GetString(type.Name));
            if (ContractSet.Contains(name))
            {
                Require(destinations.Add(name), FailureCode.DuplicateDestinationType);
                Require((type.Attributes & TypeAttributes.VisibilityMask) == TypeAttributes.Public,
                    FailureCode.DestinationNotPublic);
            }
        }
        Require(destinations.SetEquals(ContractSet), FailureCode.MissingDestinationType);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static RuntimeEvidence CheckRuntime(AssemblyImage sdkImage, AssemblyImage abstractionsImage)
    {
        using BoundedLoadContext context = new(sdkImage, abstractionsImage);
        Assembly sdk = context.LoadFromAssemblyPath(sdkImage.Path);
        Assembly abstractions = context.LoadFromAssemblyPath(abstractionsImage.Path);
        VerifyAssembly(sdk, sdkImage, context);
        VerifyAssembly(abstractions, abstractionsImage, context);
        Type[] forwarded = sdk.GetForwardedTypes();
        Require(forwarded.Length == ContractNames.Length, FailureCode.RuntimeForwardedTypeCountMismatch);
        Dictionary<string, Type> forwardedByName = new(StringComparer.Ordinal);
        foreach (Type type in forwarded)
        {
            Require(type.FullName is not null && ContractSet.Contains(type.FullName), FailureCode.RuntimeUnknownType);
            Require(forwardedByName.TryAdd(type.FullName!, type), FailureCode.RuntimeDuplicateType);
        }
        List<TypeEvidence> verified = [];
        foreach (string name in ContractNames)
        {
            Type? direct = abstractions.GetType(name, throwOnError: true, ignoreCase: false);
            Type? viaSdk = sdk.GetType(name, throwOnError: true, ignoreCase: false);
            int assemblyResolverCalls = 0;
            int typeResolverCalls = 0;
            Type? qualified = Type.GetType(name + ", " + SdkIdentity,
                requested =>
                {
                    assemblyResolverCalls++;
                    Require(requested.FullName == SdkIdentity, FailureCode.UnexpectedQualifiedAssembly);
                    return sdk;
                },
                (assembly, typeName, ignoreCase) =>
                {
                    typeResolverCalls++;
                    Require(ReferenceEquals(assembly, sdk) && typeName == name && !ignoreCase,
                        FailureCode.UnexpectedQualifiedType);
                    return assembly!.GetType(typeName, throwOnError: true, ignoreCase: false);
                }, throwOnError: true, ignoreCase: false);
            Require(assemblyResolverCalls == 1 && typeResolverCalls == 1, FailureCode.QualifiedResolverCountMismatch);
            Require(direct is not null && ReferenceEquals(direct.Assembly, abstractions), FailureCode.WrongRuntimeOrigin);
            Require(ReferenceEquals(direct, viaSdk) && ReferenceEquals(direct, forwardedByName[name]) &&
                ReferenceEquals(direct, qualified), FailureCode.RuntimeTypeIdentityMismatch);
            Require(direct.Module.ModuleVersionId == abstractionsImage.Mvid, FailureCode.RuntimeMvidMismatch);
            verified.Add(new TypeEvidence(name, true, true, true));
        }
        Assembly[] localAssemblies = context.Assemblies.ToArray();
        Require(localAssemblies.Length == 2 && localAssemblies.Contains(sdk) && localAssemblies.Contains(abstractions),
            FailureCode.UnexpectedContextAssembly);
        VerifyAssembly(sdk, sdkImage, context);
        VerifyAssembly(abstractions, abstractionsImage, context);
        return new RuntimeEvidence(true, true, true, localAssemblies.Length, verified.ToArray());
    }

    private static void VerifyAssembly(Assembly assembly, AssemblyImage image, BoundedLoadContext context)
    {
        Require(assembly.FullName == image.Identity && ReferenceEquals(AssemblyLoadContext.GetLoadContext(assembly), context),
            FailureCode.WrongRuntimeOrigin);
        Require(assembly.Location == image.Path && assembly.ManifestModule.ModuleVersionId == image.Mvid,
            FailureCode.RuntimeMvidMismatch);
        Require(assembly.GetModules().Length == 1, FailureCode.MultimoduleAssembly);
    }

    internal static string Identity(MetadataReader reader, AssemblyReference reference)
    {
        AssemblyName identity = new()
        {
            Name = reader.GetString(reference.Name), Version = reference.Version,
            CultureName = reader.GetString(reference.Culture)
        };
        byte[] key = reader.GetBlobBytes(reference.PublicKeyOrToken);
        if ((reference.Flags & AssemblyFlags.PublicKey) != 0) identity.SetPublicKey(key);
        else identity.SetPublicKeyToken(key);
        return identity.FullName;
    }

    internal static string Qualified(string ns, string name) => ns.Length == 0 ? name : ns + "." + name;
    internal static void Require([System.Diagnostics.CodeAnalysis.DoesNotReturnIf(false)] bool condition, FailureCode code)
    {
        if (!condition) throw new ProbeFailure(code);
    }
    internal static FailureCode ErrorCode(Exception exception) => exception switch
    {
        ProbeFailure failure => failure.Code,
        BadImageFormatException => FailureCode.InvalidManagedImage,
        FileNotFoundException or DirectoryNotFoundException => FailureCode.InputNotFound,
        UnauthorizedAccessException => FailureCode.InputAccessDenied,
        IOException => FailureCode.InputReadFailed,
        ReflectionTypeLoadException or TypeLoadException or FileLoadException => FailureCode.RuntimeBindingFailed,
        _ => FailureCode.UnexpectedFailure
    };
}

internal sealed class AssemblyImage : IDisposable
{
    private readonly FileStream stream;
    private readonly PEReader pe;
    internal string Path { get; }
    internal string Sha256 { get; }
    internal string Identity { get; }
    internal Guid Mvid { get; }
    internal MetadataReader Reader { get; }
    private AssemblyImage(string path, string expectedHash, string expectedIdentity)
    {
        Path = path;
        stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Program.Require(stream.Length is > 0 and <= 67108864, FailureCode.InputSizeLimit);
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(stream));
            Program.Require(Sha256 == expectedHash, FailureCode.FileHashMismatch);
            stream.Position = 0;
            pe = new PEReader(stream, PEStreamOptions.LeaveOpen);
            Program.Require(pe.HasMetadata && pe.PEHeaders.CorHeader is not null &&
                (pe.PEHeaders.CorHeader.Flags & CorFlags.ILOnly) != 0,
                FailureCode.InvalidManagedImage);
            Reader = pe.GetMetadataReader();
            Program.Require(Reader.IsAssembly, FailureCode.InvalidManagedImage);
            AssemblyDefinition definition = Reader.GetAssemblyDefinition();
            Program.Require(definition.Flags == 0 && Reader.GetBlobBytes(definition.PublicKey).Length == 0,
                FailureCode.AssemblyIdentityMismatch);
            AssemblyName name = new()
            {
                Name = Reader.GetString(definition.Name), Version = definition.Version,
                CultureName = Reader.GetString(definition.Culture)
            };
            name.SetPublicKeyToken([]);
            Identity = name.FullName;
            Program.Require(Identity == expectedIdentity, FailureCode.AssemblyIdentityMismatch);
            Mvid = Reader.GetGuid(Reader.GetModuleDefinition().Mvid);
            Program.Require(Mvid != Guid.Empty, FailureCode.InvalidMvid);
            Program.Require(Reader.AssemblyFiles.Count == 0, FailureCode.MultimoduleAssembly);
            foreach (TypeDefinitionHandle handle in Reader.TypeDefinitions)
            {
                TypeDefinition type = Reader.GetTypeDefinition(handle);
                if (Reader.GetString(type.Name) == "<Module>")
                    Program.Require(type.GetMethods().Count == 0, FailureCode.ModuleInitializerNotAllowed);
            }
            CheckInformationalVersion(Reader);
        }
        catch
        {
            pe?.Dispose();
            stream.Dispose();
            throw;
        }
    }
    internal static AssemblyImage Read(string path, string hash, string identity) => new(path, hash, identity);
    private static void CheckInformationalVersion(MetadataReader reader)
    {
        int found = 0;
        foreach (CustomAttributeHandle handle in reader.GetAssemblyDefinition().GetCustomAttributes())
        {
            CustomAttribute attribute = reader.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            MemberReference member = reader.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            TypeReference type = reader.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (reader.GetString(type.Namespace) != "System.Reflection" ||
                reader.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
            Program.Require(type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "System.Runtime" &&
                reader.GetString(member.Name) == ".ctor", FailureCode.SourceVersionMismatch);
            BlobReader value = reader.GetBlobReader(attribute.Value);
            Program.Require(value.ReadUInt16() == 1 && value.ReadSerializedString() == Program.InformationalVersion &&
                value.ReadUInt16() == 0 && value.RemainingBytes == 0, FailureCode.SourceVersionMismatch);
            found++;
        }
        Program.Require(found == 1, FailureCode.SourceVersionMismatch);
    }
    internal void VerifyUnchanged()
    {
        using FileStream current = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        Program.Require(current.Length <= 67108864 && Convert.ToHexStringLower(SHA256.HashData(current)) == Sha256,
            FailureCode.ArtifactChanged);
    }
    internal AssemblyEvidence Evidence() => new(Sha256, Identity, Mvid.ToString("D"), Program.InformationalVersion);
    public void Dispose() { pe.Dispose(); stream.Dispose(); }
}

internal sealed class BoundedLoadContext : AssemblyLoadContext, IDisposable
{
    private readonly AssemblyImage sdk;
    private readonly AssemblyImage abstractions;
    private readonly Dictionary<string, string> trustedRuntimePaths;
    internal BoundedLoadContext(AssemblyImage sdk, AssemblyImage abstractions) : base(isCollectible: true)
    {
        this.sdk = sdk;
        this.abstractions = abstractions;
        string runtimeDirectory = System.IO.Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        trustedRuntimePaths = new(StringComparer.Ordinal);
        // Limit fallback to exact full identities from the active .NET runtime directory,
        // never the probe directory, input directories, or other application TPA entries.
        string trusted = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string ?? "";
        foreach (string path in trusted.Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (System.IO.Path.GetDirectoryName(path) != runtimeDirectory) continue;
            AssemblyName name = AssemblyName.GetAssemblyName(path);
            if (trustedRuntimePaths.TryGetValue(name.FullName, out string? existing))
                Program.Require(existing == path, FailureCode.RuntimeAllowlistMissing);
            else trustedRuntimePaths.Add(name.FullName, path);
        }
        Program.Require(trustedRuntimePaths.Count > 0, FailureCode.RuntimeAllowlistMissing);
    }
    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name == "SharpLink.Sdk")
        {
            Program.Require(name.FullName == sdk.Identity, FailureCode.UnapprovedAssemblyResolution);
            return LoadFromAssemblyPath(sdk.Path);
        }
        if (name.Name == "SharpLink.Abstractions")
        {
            Program.Require(name.FullName == abstractions.Identity, FailureCode.UnapprovedAssemblyResolution);
            return LoadFromAssemblyPath(abstractions.Path);
        }
        Program.Require(trustedRuntimePaths.TryGetValue(name.FullName, out string? path), FailureCode.UnapprovedAssemblyResolution);
        Assembly runtime = Default.LoadFromAssemblyPath(path!);
        Program.Require(runtime.FullName == name.FullName && runtime.Location == path, FailureCode.UnapprovedAssemblyResolution);
        return runtime;
    }
    protected override nint LoadUnmanagedDll(string name) => throw new ProbeFailure(FailureCode.UnmanagedResolutionNotAllowed);
    public void Dispose() => Unload();
}

internal sealed class ProbeFailure(FailureCode code) : Exception { internal FailureCode Code { get; } = code; }
internal enum FailureCode
{
    InvalidArguments, InputNotFound, InputAccessDenied, InputReadFailed, InputSizeLimit,
    FileHashMismatch, InvalidManagedImage, AssemblyIdentityMismatch, InvalidMvid, MultimoduleAssembly,
    ModuleInitializerNotAllowed, SourceVersionMismatch, ExportedTypeCountMismatch, UnknownExportedType,
    DuplicateExportedType, NotAssemblyForwarder, WrongDestinationIdentity, ExportedTypeSetMismatch,
    LocalSdkImpostor, DuplicateDestinationType, DestinationNotPublic, MissingDestinationType,
    RuntimeForwardedTypeCountMismatch, RuntimeUnknownType, RuntimeDuplicateType, UnexpectedQualifiedAssembly,
    UnexpectedQualifiedType, QualifiedResolverCountMismatch, WrongRuntimeOrigin, RuntimeTypeIdentityMismatch,
    RuntimeMvidMismatch, UnexpectedContextAssembly, ArtifactChanged, RuntimeAllowlistMissing,
    UnapprovedAssemblyResolution, UnmanagedResolutionNotAllowed, RuntimeBindingFailed, UnexpectedFailure,
    SelfTestFailed
}
internal sealed record Inputs(string SdkPath, string SdkSha256, string AbstractionsPath, string AbstractionsSha256);
internal sealed record FailureResult(int SchemaVersion, string Mode, bool Success, string Code, string SourceCommit,
    string? SelfTestStage, string? ErrorCategory);
internal sealed record ProbeResult(int SchemaVersion, string Mode, bool Success, string Code, string SourceCommit,
    string EvidenceScope, bool Historical203AbiProven, bool OrdinaryPackageReferenceConsumerProven,
    int ExpectedForwarderCount, AssemblyEvidence Sdk, AssemblyEvidence Abstractions, string RuntimeVersion, RuntimeEvidence Runtime);
internal sealed record AssemblyEvidence(string Sha256, string FullIdentity, string Mvid, string InformationalVersion);
internal sealed record RuntimeEvidence(bool MetadataExact20, bool FreshBoundedCollectibleContext,
    bool LoadedOriginsVerified, int ContextAssemblyCount, TypeEvidence[] Types);
internal sealed record TypeEvidence(string Name, bool AssemblyGetTypeSameAsDirect,
    bool GetForwardedTypesSameAsDirect, bool SdkQualifiedTypeGetTypeSameAsDirect);
