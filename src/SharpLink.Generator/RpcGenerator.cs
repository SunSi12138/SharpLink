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
        var invalidStreamCountMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidStreamCountMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidTimeoutCancellationMethods = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidTimeoutCancellationMethods(attributeContext, ct))
            .Where(x => x.Length > 0);
        var invalidGenericUsage = context.SyntaxProvider.ForAttributeWithMetadataName(
                RpcContractAttributeMetadataName,
                static (node, _) => node is InterfaceDeclarationSyntax,
                static (attributeContext, ct) => GetInvalidGenericUsage(attributeContext, ct))
            .Where(x => x.Length > 0);

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
        context.RegisterSourceOutput(invalidTimeoutCancellationMethods, static (spc, methods) =>
        {
            foreach (var method in methods)
            {
                var diagnostic = Diagnostic.Create(
                    TimeoutRequiresCancellationTokenRule,
                    method.Location,
                    method.MethodName);
                spc.ReportDiagnostic(diagnostic);
            }
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
    }
}
