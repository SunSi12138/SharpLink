using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

namespace ForwarderProbe;

// These are deliberately manufactured metadata fixtures, not shipping artifacts,
// source-built SharpLink types, an old consumer, or evidence of 2.0.3 ABI compatibility.
internal static class SelfTests
{
    internal static string Stage { get; private set; } = "setup";
    internal static SelfTestResult Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "sharplink-forwarder-self-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            List<SelfTestCase> results = [];
            Stage = "generate-positive-abstractions";
            string abstractions = Write(root, "abstractions", FixtureKind.Abstractions);
            Stage = "generate-positive-sdk";
            string sdk = Write(root, "sdk", FixtureKind.PositiveSdk);
            Inputs positive = Input(sdk, abstractions);
            Stage = "positive-production-pipeline";
            ProbeResult pass = Program.Run(positive);
            Program.Require(pass.Success && pass.Runtime.Types.Length == 20 &&
                pass.Runtime.Types.All(t => t.AssemblyGetTypeSameAsDirect && t.GetForwardedTypesSameAsDirect &&
                    t.SdkQualifiedTypeGetTypeSameAsDirect), FailureCode.SelfTestFailed);
            results.Add(new("positive-exact20-all-three-runtime-resolution-paths", "Pass", true));
            Expect(results, "repeat-in-new-collectible-context", () => { _ = Program.Run(positive); }, null);
            Expect(results, "sdk-hash-mismatch", () => Program.Run(positive with { SdkSha256 = new string('0', 64) }), FailureCode.FileHashMismatch);
            Expect(results, "abstractions-hash-mismatch", () => Program.Run(positive with { AbstractionsSha256 = new string('0', 64) }), FailureCode.FileHashMismatch);
            foreach ((string name, FixtureKind kind, FailureCode expected) in new[]
            {
                ("missing-forwarder", FixtureKind.MissingForwarder, FailureCode.ExportedTypeCountMismatch),
                ("extra-forwarder", FixtureKind.ExtraForwarder, FailureCode.ExportedTypeCountMismatch),
                ("duplicate-forwarder", FixtureKind.DuplicateForwarder, FailureCode.DuplicateExportedType),
                ("unknown-forwarder", FixtureKind.UnknownForwarder, FailureCode.UnknownExportedType),
                ("local-same-shape-impostor", FixtureKind.LocalImpostor, FailureCode.LocalSdkImpostor),
                ("wrong-destination-name", FixtureKind.WrongDestinationName, FailureCode.WrongDestinationIdentity),
                ("wrong-destination-version", FixtureKind.WrongDestinationVersion, FailureCode.WrongDestinationIdentity),
                ("wrong-destination-culture", FixtureKind.WrongDestinationCulture, FailureCode.WrongDestinationIdentity),
                ("wrong-destination-token", FixtureKind.WrongDestinationToken, FailureCode.WrongDestinationIdentity),
                ("not-a-forwarder", FixtureKind.NotForwarder, FailureCode.NotAssemblyForwarder),
                ("wrong-sdk-assembly-version", FixtureKind.WrongAssemblyVersion, FailureCode.AssemblyIdentityMismatch),
                ("wrong-source-version", FixtureKind.WrongSourceVersion, FailureCode.SourceVersionMismatch)
            })
            {
                string path = Write(root, name, kind);
                Expect(results, name, () => Program.Run(Input(path, abstractions)), expected);
            }
            string missingDestination = Write(root, "missing-destination", FixtureKind.MissingDestination);
            Expect(results, "missing-destination-type", () => Program.Run(Input(sdk, missingDestination)), FailureCode.MissingDestinationType);
            string privateDestination = Write(root, "private-destination", FixtureKind.PrivateDestination);
            Expect(results, "nonpublic-destination-type", () => Program.Run(Input(sdk, privateDestination)), FailureCode.DestinationNotPublic);
            string missingFile = Path.Combine(root, "absent", "SharpLink.Abstractions.dll");
            Expect(results, "missing-abstractions-dll", () => Program.Run(positive with { AbstractionsPath = missingFile }), FailureCode.InputNotFound);
            string missingSdk = Path.Combine(root, "absent", "SharpLink.Sdk.dll");
            Expect(results, "missing-sdk-dll", () => Program.Run(positive with { SdkPath = missingSdk }), FailureCode.InputNotFound);
            string[] arguments = ["--sdk", sdk, "--sdk-sha256", positive.SdkSha256, "--abstractions", abstractions,
                "--abstractions-sha256", positive.AbstractionsSha256];
            Program.Require(Program.Parse(arguments) == positive, FailureCode.SelfTestFailed);
            results.Add(new("valid-fixed-cli", "Pass", true));
            Expect(results, "cli-empty", () => Program.Parse([]), FailureCode.InvalidArguments);
            Expect(results, "cli-extra-argument", () => Program.Parse([.. arguments, "extra"]), FailureCode.InvalidArguments);
            string[] unknown = (string[])arguments.Clone(); unknown[0] = "--unknown";
            Expect(results, "cli-unknown-name", () => Program.Parse(unknown), FailureCode.InvalidArguments);
            string[] duplicate = (string[])arguments.Clone(); duplicate[2] = "--sdk";
            Expect(results, "cli-duplicate-name", () => Program.Parse(duplicate), FailureCode.InvalidArguments);
            string[] invalidHash = (string[])arguments.Clone(); invalidHash[3] = new string('z', 64);
            Expect(results, "cli-nonhex-hash", () => Program.Parse(invalidHash), FailureCode.InvalidArguments);
            string[] relativePath = (string[])arguments.Clone(); relativePath[1] = "SharpLink.Sdk.dll";
            Expect(results, "cli-relative-path", () => Program.Parse(relativePath), FailureCode.InvalidArguments);
            string[] selfTestExtra = ["--self-test", "extra"];
            Expect(results, "cli-selftest-extra-argument", () => Program.Parse(selfTestExtra), FailureCode.InvalidArguments);
            using (AssemblyImage sdkImage = AssemblyImage.Read(sdk, positive.SdkSha256, Program.SdkIdentity))
            using (AssemblyImage abstractionsImage = AssemblyImage.Read(abstractions, positive.AbstractionsSha256, Program.AbstractionsIdentity))
            using (BoundedLoadContext context = new(sdkImage, abstractionsImage))
            {
                // The main probe has already checked the production pipeline above. These
                // explicit boundary cases also test that app/default-context fallback is denied.
                ExpectResolutionDenied(results, "alc-rejects-probe-application-assembly", context,
                    typeof(SelfTests).Assembly.GetName());
                ExpectResolutionDenied(results, "alc-rejects-arbitrary-missing-dependency", context,
                    new AssemblyName("SharpLink.Missing, Version=3.0.0.0, Culture=neutral, PublicKeyToken=null"));
                ExpectResolutionDenied(results, "alc-rejects-mismatched-abstractions-identity", context,
                    new AssemblyName("SharpLink.Abstractions, Version=9.0.0.0, Culture=neutral, PublicKeyToken=null"));
            }
            return new SelfTestResult(1, "synthetic-self-test", true, "Pass", results.Count,
                "ManufacturedMetadataFixturesNotHistoricalConsumer", results.ToArray());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void ExpectResolutionDenied(List<SelfTestCase> results, string name, BoundedLoadContext context, AssemblyName assembly)
    {
        Stage = name;
        bool rejected = false;
        try { _ = context.LoadFromAssemblyName(assembly); }
        catch (Exception error)
        {
            rejected = error is ProbeFailure { Code: FailureCode.UnapprovedAssemblyResolution } ||
                error is FileLoadException { InnerException: ProbeFailure { Code: FailureCode.UnapprovedAssemblyResolution } };
        }
        Program.Require(rejected, FailureCode.SelfTestFailed);
        results.Add(new(name, "UnapprovedAssemblyResolution", true));
    }
    private static void Expect(List<SelfTestCase> results, string name, Action action, FailureCode? expected)
    {
        Stage = name;
        FailureCode? actual = null;
        try { action(); }
        catch (Exception error) { actual = Program.ErrorCode(error); }
        Program.Require(actual == expected, FailureCode.SelfTestFailed);
        results.Add(new(name, expected?.ToString() ?? "Pass", true));
    }
    private static Inputs Input(string sdk, string abstractions) => new(sdk,
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(sdk))), abstractions,
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(abstractions))));

    private static string Write(string root, string folder, FixtureKind kind)
    {
        bool isAbstractions = kind is FixtureKind.Abstractions or FixtureKind.MissingDestination or FixtureKind.PrivateDestination;
        string assemblyName = isAbstractions ? "SharpLink.Abstractions" : "SharpLink.Sdk";
        MetadataBuilder md = new();
        md.AddModule(0, md.GetOrAddString(assemblyName + ".dll"), md.GetOrAddGuid(Guid.NewGuid()), default, default);
        AssemblyDefinitionHandle assembly = md.AddAssembly(md.GetOrAddString(assemblyName),
            new Version(kind == FixtureKind.WrongAssemblyVersion ? 4 : 3, 0, 0, 0), default, default,
            0, AssemblyHashAlgorithm.Sha256);
        AssemblyReferenceHandle runtime = md.AddAssemblyReference(md.GetOrAddString("System.Runtime"),
            new Version(10, 0, 0, 0), default, md.GetOrAddBlob(Convert.FromHexString("b03f5f7f11d50a3a")), 0, default);
        TypeReferenceHandle informational = md.AddTypeReference(runtime, md.GetOrAddString("System.Reflection"),
            md.GetOrAddString("AssemblyInformationalVersionAttribute"));
        MemberReferenceHandle constructor = md.AddMemberReference(informational, md.GetOrAddString(".ctor"),
            md.GetOrAddBlob(new byte[] { 0x20, 0x01, 0x01, 0x0e }));
        BlobBuilder value = new(); value.WriteUInt16(1);
        value.WriteSerializedString(kind == FixtureKind.WrongSourceVersion ? "3.0.0+incorrect" : Program.InformationalVersion);
        value.WriteUInt16(0);
        md.AddCustomAttribute(assembly, constructor, md.GetOrAddBlob(value));
        md.AddTypeDefinition(TypeAttributes.NotPublic, default, md.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        void AddType(string name, bool isPublic = true)
        {
            int split = name.LastIndexOf('.');
            md.AddTypeDefinition((isPublic ? TypeAttributes.Public : TypeAttributes.NotPublic) |
                TypeAttributes.Interface | TypeAttributes.Abstract,
                md.GetOrAddString(name[..split]), md.GetOrAddString(name[(split + 1)..]), default,
                MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        }
        if (isAbstractions)
        {
            for (int i = 0; i < Program.ContractNames.Length; i++)
            {
                if (kind == FixtureKind.MissingDestination && i == 0) continue;
                AddType(Program.ContractNames[i], !(kind == FixtureKind.PrivateDestination && i == 0));
            }
        }
        else
        {
            AssemblyReferenceHandle destination = md.AddAssemblyReference(
                md.GetOrAddString(kind == FixtureKind.WrongDestinationName ? "SharpLink.Other" : "SharpLink.Abstractions"),
                new Version(kind == FixtureKind.WrongDestinationVersion ? 4 : 3, 0, 0, 0),
                kind == FixtureKind.WrongDestinationCulture ? md.GetOrAddString("fr") : default,
                kind == FixtureKind.WrongDestinationToken ? md.GetOrAddBlob(Convert.FromHexString("b03f5f7f11d50a3a")) : default,
                0, default);
            List<string> names = [.. Program.ContractNames];
            if (kind == FixtureKind.MissingForwarder) names.RemoveAt(0);
            if (kind == FixtureKind.ExtraForwarder) names.Add("SharpLink.Sdk.Unexpected");
            if (kind == FixtureKind.DuplicateForwarder) names[1] = names[0];
            if (kind == FixtureKind.UnknownForwarder) names[0] = "SharpLink.Sdk.Unexpected";
            foreach (string name in names)
            {
                int split = name.LastIndexOf('.');
                md.AddExportedType(TypeAttributes.Public | (kind == FixtureKind.NotForwarder ? 0 : (TypeAttributes)0x00200000),
                    md.GetOrAddString(name[..split]), md.GetOrAddString(name[(split + 1)..]), destination, 0);
            }
            if (kind == FixtureKind.LocalImpostor) AddType(Program.ContractNames[0]);
        }
        ManagedPEBuilder pe = new(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(md), new BlobBuilder(), flags: CorFlags.ILOnly);
        BlobBuilder content = new(); pe.Serialize(content);
        string directory = Path.Combine(root, folder); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, assemblyName + ".dll");
        File.WriteAllBytes(path, content.ToArray());
        return path;
    }
    private enum FixtureKind
    {
        Abstractions, PositiveSdk, MissingForwarder, ExtraForwarder, DuplicateForwarder, UnknownForwarder,
        LocalImpostor, WrongDestinationName, WrongDestinationVersion, WrongDestinationCulture, WrongDestinationToken,
        NotForwarder, WrongAssemblyVersion, WrongSourceVersion, MissingDestination, PrivateDestination
    }
}
internal sealed record SelfTestResult(int SchemaVersion, string Mode, bool Success, string Code,
    int Passed, string EvidenceScope, SelfTestCase[] Tests);
internal sealed record SelfTestCase(string Name, string ExpectedCode, bool Passed);
