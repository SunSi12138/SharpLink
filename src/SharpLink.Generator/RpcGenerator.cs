namespace SharpLink.Generator;

[Generator]
public partial class RpcGenerator : IIncrementalGenerator
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;
    private const string RpcContractAttributeMetadataName = "SharpLink.Sdk.RpcContractAttribute";
    private const string RpcServiceAttributeMetadataName = "SharpLink.Sdk.RpcServiceAttribute";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interfaces = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInterfaceModelOrNull(attributeContext, ct))
            .Where(m => m != null);

        var referencedInterfaces = context.CompilationProvider.Select(static (compilation, ct) =>
            GetReferencedInterfaceModels(compilation, ct));
        var referencedServices = context.CompilationProvider.Select(static (compilation, ct) =>
            GetReferencedServiceModels(compilation, ct));
        var generatedCodecs = context.CompilationProvider.Select(static (compilation, ct) =>
            AnalyzeGeneratedCodecs(compilation, ct));

        var services = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcServiceAttributeMetadataName,
                static (node, _) => node is ClassDeclarationSyntax,
                static (attributeContext, ct) => GetServiceModelOrNull(attributeContext, ct))
            .Where(m => m != null);

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

        context.RegisterSourceOutput(services, (spc, model) =>
        {
            var code = GenerateStub(model!);
            spc.AddSource(GetStubHintName(model!), SourceText.From(code, Encoding.UTF8));
        });
        context.RegisterSourceOutput(referencedServices, (spc, models) =>
        {
            foreach (var model in models)
            {
                var code = GenerateStub(model);
                spc.AddSource(GetStubHintName(model), SourceText.From(code, Encoding.UTF8));
            }
        });

        context.RegisterSourceOutput(interfaces, (spc, model) =>
        {
            var code = GenerateProxy(model!);
            spc.AddSource(GetProxyHintName(model!), SourceText.From(code, Encoding.UTF8));
        });

        context.RegisterSourceOutput(referencedInterfaces, (spc, models) =>
        {
            foreach (var model in models)
            {
                var code = GenerateProxy(model);
                spc.AddSource(GetProxyHintName(model), SourceText.From(code, Encoding.UTF8));
            }
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
    }
}
