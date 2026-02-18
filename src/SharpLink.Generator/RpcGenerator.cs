namespace SharpLink.Generator;

[Generator]
public partial class RpcGenerator : IIncrementalGenerator
{
    private static readonly CultureInfo InvariantCulture = CultureInfo.InvariantCulture;

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var interfaces = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInterfaceModelOrNull)
            .Where(m => m != null);

        var services = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsClassCandidate, transform: GetServiceModelOrNull)
            .Where(m => m != null);

        var invalidMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidRpcMethods)
            .Where(x => x.Length > 0);
        var invalidCancellationTokenMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidCancellationTokenMethods)
            .Where(x => x.Length > 0);
        var invalidStreamCountMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidStreamCountMethods)
            .Where(x => x.Length > 0);
        var invalidTimeoutCancellationMethods = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidTimeoutCancellationMethods)
            .Where(x => x.Length > 0);
        var invalidGenericUsage = context.SyntaxProvider
            .CreateSyntaxProvider(predicate: IsInterfaceCandidate, transform: GetInvalidGenericUsage)
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
            spc.AddSource($"{model!.ServiceName}_Stub.g.cs", SourceText.From(code, Encoding.UTF8));
        });

        context.RegisterSourceOutput(interfaces, (spc, model) =>
        {
            var code = GenerateProxy(model!);
            spc.AddSource($"{model!.Name}_Proxy.g.cs", SourceText.From(code, Encoding.UTF8));
        });
    }
}
