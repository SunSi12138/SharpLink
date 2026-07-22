namespace SharpLink.Generator;

[Generator]
public partial class RpcGenerator : IIncrementalGenerator
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private const string RpcContractAttributeMetadataName = "SharpLink.Sdk.RpcContractAttribute";
    private const string RpcServiceAttributeMetadataName = "SharpLink.Sdk.RpcServiceAttribute";
    private const string ClusterContractAssemblyAttributeMetadataName =
        "SharpLink.Sdk.SharpLinkClusterContractAssemblyAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInterfaceModelOrNull(attributeContext, ct))
            .Where(m => m != null);

        var generatedCodecs = context.CompilationProvider.Select(static (compilation, ct) =>
                AnalyzeGeneratedCodecs(compilation, ct))
            .WithComparer(DtoGenerationResultComparer.Instance);

        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcServiceAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (attributeContext, ct) => GetServiceModelOrNull(attributeContext, ct))
            .Where(m => m != null);
        var unions = context.SyntaxProvider.ForAttributeWithMetadataName(
                "SharpLink.Sdk.RpcUnionCaseAttribute",
                static (node, _) => node is TypeDeclarationSyntax,
                static (attributeContext, ct) => GetUnionModelOrNull(attributeContext, ct))
            .Where(static model => model is not null);
        var serviceDiagnostics = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcServiceAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (attributeContext, ct) => GetRpcServiceDiagnosticOrNull(attributeContext, ct))
            .Where(static diagnostic => diagnostic is not null);
        var staticRouteConflicts = context.CompilationProvider.Select(static (compilation, ct) =>
            AnalyzeStaticRouteConflicts(compilation, ct));
        var clusterRoutes = context.CompilationProvider.Select(static (compilation, ct) =>
            AnalyzeClusterRoutes(compilation, ct));

        var invalidMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidRpcMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidCancellationTokenMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidCancellationTokenMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidCallOptionsMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidCallOptionsMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidControlParameterOrderMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidControlParameterOrderMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidStreamCountMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidStreamCountMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var nonCancellableRpcMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetNonCancellableRpcMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var streamingWithoutCancellationMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetStreamingWithoutCancellationMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var conflictingCancellationContractMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetConflictingCancellationContractMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidGenericUsage = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidGenericUsage(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidRpcContractInheritance = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidRpcContractInheritance(attributeContext, ct))
            .Where(static x => x is not null);

        context.RegisterSourceOutput(invalidMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    InvalidReturnTypeRule,
                    method.Location,
                    method.MethodName,
                    method.ReturnType);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(invalidCancellationTokenMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    MultipleCancellationTokensRule,
                    method.Location,
                    method.MethodName);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(invalidCallOptionsMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
                spc.ReportDiagnostic(Diagnostic.Create(MultipleCallOptionsRule, method.Location, method.MethodName));
        });
        context.RegisterSourceOutput(invalidControlParameterOrderMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
                spc.ReportDiagnostic(Diagnostic.Create(ControlParameterOrderRule, method.Location, method.MethodName));
        });
        context.RegisterSourceOutput(invalidStreamCountMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    StreamParameterCountRule,
                    method.Location,
                    method.MethodName,
                    method.StreamParameterCount);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(nonCancellableRpcMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
                spc.ReportDiagnostic(Diagnostic.Create(MissingCancellationTokenRule, method.Location, method.MethodName));
        });
        context.RegisterSourceOutput(streamingWithoutCancellationMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
                spc.ReportDiagnostic(Diagnostic.Create(
                    StreamingMissingCancellationTokenRule,
                    method.Location,
                    method.MethodName));
        });
        context.RegisterSourceOutput(conflictingCancellationContractMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
                spc.ReportDiagnostic(Diagnostic.Create(
                    ConflictingCancellationContractRule,
                    method.Location,
                    method.MethodName));
        });
        context.RegisterSourceOutput(invalidGenericUsage, static (spc, models) =>
        {
            foreach (var model in models)
            {
                var diagnostic = Diagnostic.Create(
                    GenericUsageInRpcRule,
                    model.Location,
                    model.SymbolName,
                    model.TypeName);
                spc.ReportDiagnostic(diagnostic);
            }
        });
        context.RegisterSourceOutput(invalidRpcContractInheritance, static (spc, model) =>
        {
            var diagnostic = Diagnostic.Create(
                RpcContractMustInheritIServiceRule,
                model!.Value.Location,
                model.Value.InterfaceName);
            spc.ReportDiagnostic(diagnostic);
        });

        context.RegisterSourceOutput(serviceDiagnostics, static (spc, diagnostic) =>
        {
            var value = diagnostic!.Value;
            var descriptor = value.Kind switch
            {
                RpcServiceDiagnosticKind.MissingContract => RpcServiceMissingContractRule,
                RpcServiceDiagnosticKind.MultipleContracts => RpcServiceMultipleContractsRule,
                RpcServiceDiagnosticKind.InvalidType => RpcServiceTypeRule,
                RpcServiceDiagnosticKind.InvalidConstructor => RpcServiceConstructorRule,
                _ => RpcServiceLifetimeRule
            };
            spc.ReportDiagnostic(Diagnostic.Create(
                descriptor,
                value.Location,
                value.ServiceName,
                value.Detail));
        });

        context.RegisterSourceOutput(staticRouteConflicts, static (spc, conflicts) =>
        {
            foreach (var conflict in conflicts)
            {
                var descriptor = conflict.Kind switch
                {
                    StaticRouteConflictKind.Method => StaticMethodConflictRule,
                    StaticRouteConflictKind.Service => StaticServiceConflictRule,
                    _ => StaticContractConflictRule
                };
                spc.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    conflict.Location,
                    conflict.Name,
                    conflict.Id,
                    conflict.ExistingFingerprint,
                    conflict.IncomingFingerprint));
            }
        });

        context.RegisterSourceOutput(clusterRoutes, static (spc, analysis) =>
        {
            foreach (var diagnostic in analysis.Diagnostics)
                spc.ReportDiagnostic(Diagnostic.Create(diagnostic.Rule, diagnostic.Location, diagnostic.Arguments));

            if (!analysis.Routes.IsDefaultOrEmpty)
            {
                spc.AddSource(
                    "SharpLink.GeneratedClusterRouteManifest.g.cs",
                    SourceText.From(GenerateClusterRouteManifest(analysis.Routes), Encoding.UTF8));
            }
        });

        context.RegisterSourceOutput(interfaces, (spc, model) =>
        {
            var proxy = GenerateProxy(model!);
            spc.AddSource(GetProxyHintName(model!), SourceText.From(proxy, Encoding.UTF8));
            var stub = GenerateStub(model!);
            spc.AddSource(GetStubHintName(model!), SourceText.From(stub, Encoding.UTF8));
        });

        context.RegisterSourceOutput(generatedCodecs, static (spc, result) =>
        {
            foreach (var diagnostic in result.Diagnostics)
            {
                var descriptor = diagnostic.Kind switch
                {
                    DtoDiagnosticKind.Cycle => CyclicDtoGraphRule,
                    DtoDiagnosticKind.MemberIdCollision => DuplicateDtoMemberIdRule,
                    DtoDiagnosticKind.Constructor => DtoConstructionRule,
                    DtoDiagnosticKind.Depth => DtoDepthRule,
                    _ => UnsupportedGeneratedDtoRule
                };
                spc.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    diagnostic.Location,
                    diagnostic.TypeName,
                    diagnostic.Detail));
            }

            if (!result.Codecs.IsDefaultOrEmpty)
            {
                spc.AddSource(
                    "SharpLink.GeneratedCodecs.g.cs",
                    SourceText.From(GenerateCodecs(result.Codecs), Encoding.UTF8));
            }
        });

        var manifest = interfaces.Collect().Combine(services.Collect()).Combine(generatedCodecs);
        context.RegisterSourceOutput(manifest, static (spc, value) =>
        {
            var code = GenerateAssemblyManifest(value.Left.Left, value.Left.Right, value.Right.Codecs);
            if (!string.IsNullOrEmpty(code))
            {
                spc.AddSource(
                    "SharpLink.GeneratedAssemblyManifest.g.cs",
                    SourceText.From(code, Encoding.UTF8));
            }
        });

        var contractManifestModels = interfaces.Collect()
            .Combine(services.Collect())
            .Combine(generatedCodecs)
            .Combine(unions.Collect())
            .Select(static (value, _) => new ContractManifestModels(
                value.Left.Left.Left,
                value.Left.Left.Right,
                value.Left.Right.Codecs,
                value.Left.Right.Enums,
                value.Right));
        var contractManifestOptions = context.AnalyzerConfigOptionsProvider
            .Select(static (provider, _) => GetContractManifestOptions(provider));
        var contractManifestAnalysis = contractManifestModels
            .Combine(context.AdditionalTextsProvider.Collect())
            .Combine(contractManifestOptions)
            .Select(static (value, ct) => AnalyzeContractManifest(
                value.Left.Left.Interfaces,
                value.Left.Left.Services,
                value.Left.Left.Codecs,
                value.Left.Left.Enums,
                value.Left.Left.Unions,
                value.Left.Right,
                value.Right,
                ct))
            .WithTrackingName("SharpLink.ContractManifestAnalysis");
        context.RegisterSourceOutput(contractManifestAnalysis, static (spc, analysis) =>
        {
            foreach (var item in analysis.Diagnostics)
            {
                var descriptor = item.Kind switch
                {
                    ContractCompatibilityKind.BaselineInvalid => ContractBaselineInvalidRule,
                    ContractCompatibilityKind.BaselineVersion => ContractBaselineVersionRule,
                    ContractCompatibilityKind.ContractId => ContractIdCompatibilityRule,
                    ContractCompatibilityKind.MethodId => MethodIdCompatibilityRule,
                    ContractCompatibilityKind.MemberId => MemberIdCompatibilityRule,
                    ContractCompatibilityKind.CallShape => CallShapeCompatibilityRule,
                    ContractCompatibilityKind.WireType => WireTypeCompatibilityRule,
                    ContractCompatibilityKind.Required => RequiredMemberCompatibilityRule,
                    ContractCompatibilityKind.EnumUnderlyingType => EnumCompatibilityRule,
                    ContractCompatibilityKind.UnionTag => UnionTagCompatibilityRule,
                    ContractCompatibilityKind.MethodRemoved => MethodRemovedCompatibilityRule,
                    ContractCompatibilityKind.ContractRemoved => ContractRemovedCompatibilityRule,
                    ContractCompatibilityKind.ServiceRouteRemoved => ServiceRouteRemovedCompatibilityRule,
                    _ => throw new InvalidOperationException("Unknown contract compatibility diagnostic kind.")
                };
                spc.ReportDiagnostic(Diagnostic.Create(
                    descriptor,
                    item.Location,
                    item.Item,
                    item.Detail,
                    item.Fix));
            }
            try
            {
                WriteContractManifest(analysis.OutputPath, analysis.Json);
            }
            catch (Exception exception) when (exception is System.IO.IOException or UnauthorizedAccessException or ArgumentException)
            {
                spc.ReportDiagnostic(Diagnostic.Create(
                    ContractManifestOutputRule,
                    Location.None,
                    analysis.OutputPath,
                    exception.Message));
            }
            spc.AddSource(
                "SharpLink.ContractManifest.g.cs",
                SourceText.From(GenerateContractManifestSource(analysis.Json), Encoding.UTF8));
        });
    }
}
