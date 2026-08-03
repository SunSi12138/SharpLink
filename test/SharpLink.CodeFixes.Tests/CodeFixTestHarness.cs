using System.Runtime.Loader;

namespace SharpLink.CodeFixes.Tests;

internal sealed class CodeFixTestWorkspace : IDisposable
{
    private const string MarkerStart = "[|";
    private const string MarkerEnd = "|]";

    private static readonly string SdkSource = """
using System;

namespace SharpLink.Sdk
{
    public interface IService { }

    [AttributeUsage(AttributeTargets.Interface)]
    public sealed class RpcContractAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute
    {
        public SharpLinkServiceLifetime Lifetime { get; set; }
    }

    public enum SharpLinkServiceLifetime
    {
        Singleton,
        Connection,
        Call
    }

    public readonly record struct SharpLinkCallOptions;

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonCancellableAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OnewayAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TimeoutAttribute : Attribute
    {
        public TimeoutAttribute() { }
        public TimeoutAttribute(double seconds) { }
    }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct)]
    public sealed class RpcSerializableAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcMemberAttribute : Attribute
    {
        public RpcMemberAttribute(int id) { }
    }

    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
    public sealed class RpcRequiredAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true)]
    public sealed class RpcUnionCaseAttribute : Attribute
    {
        public RpcUnionCaseAttribute(int tag, Type caseType) { }
    }

    [AttributeUsage(AttributeTargets.Assembly | AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true)]
    public sealed class RpcCodecAdapterAttribute : Attribute
    {
        public RpcCodecAdapterAttribute(Type adapterType) { }
        public RpcCodecAdapterAttribute(Type targetType, Type adapterType) { }
    }
}

namespace SharpLink.Abstractions
{
    public interface IRpcCodecAdapter { }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class RpcServiceAttribute : Attribute
    {
        public RpcServiceLifetime Lifetime { get; set; }
    }

    public enum RpcServiceLifetime
    {
        Singleton,
        Connection,
        Call
    }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class NonCancellableAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class OnewayAttribute : Attribute { }

    [AttributeUsage(AttributeTargets.Method)]
    public sealed class TimeoutAttribute : Attribute
    {
        public TimeoutAttribute() { }
        public TimeoutAttribute(double seconds) { }
    }
}
""";

    private readonly AdhocWorkspace _workspace;
    private readonly Dictionary<string, DocumentId> _documents;
    private readonly Dictionary<string, TextSpan> _markers;

    private CodeFixTestWorkspace(
        AdhocWorkspace workspace,
        Solution solution,
        ProjectId projectId,
        Dictionary<string, DocumentId> documents,
        Dictionary<string, TextSpan> markers)
    {
        _workspace = workspace;
        Solution = solution;
        ProjectId = projectId;
        _documents = documents;
        _markers = markers;
    }

    internal Solution Solution { get; private set; }

    internal ProjectId ProjectId { get; }

    internal void EnableUnsafeCode()
    {
        var project = Solution.GetProject(ProjectId)
                      ?? throw new InvalidOperationException("Test project was unavailable.");
        var options = project.CompilationOptions as CSharpCompilationOptions
                      ?? throw new InvalidOperationException("C# compilation options were unavailable.");
        Solution = project.WithCompilationOptions(options.WithAllowUnsafe(true)).Solution;
    }

    internal void AddMetadataReferenceFromSource(string assemblyName, string source, string? alias = null)
    {
        var syntaxTree = CSharpSyntaxTree.ParseText(
            source,
            new CSharpParseOptions(LanguageVersion.Preview));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [syntaxTree],
            GetPlatformReferences(),
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        Ensure(emit.Success,
            $"Expected metadata fixture '{assemblyName}' to compile. Actual: {FormatDiagnostics(emit.Diagnostics)}");
        Solution = Solution.AddMetadataReference(
            ProjectId,
            MetadataReference.CreateFromImage(
                stream.ToArray(),
                properties: alias is null
                    ? default
                    : new MetadataReferenceProperties(aliases: ImmutableArray.Create(alias))));
    }

    internal static CodeFixTestWorkspace Create(params (string Name, string Source)[] documents)
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId("CodeFixTests");
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "CodeFixTests",
            "CodeFixTests",
            LanguageNames.CSharp,
            parseOptions: new CSharpParseOptions(LanguageVersion.Preview),
            compilationOptions: new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable));
        var solution = workspace.CurrentSolution.AddProject(projectInfo)
            .AddMetadataReferences(projectId, GetPlatformReferences());
        var ids = new Dictionary<string, DocumentId>(StringComparer.Ordinal);
        var markers = new Dictionary<string, TextSpan>(StringComparer.Ordinal);

        AddDocument("SharpLinkSdk.cs", SdkSource);
        foreach (var (name, source) in documents)
            AddDocument(name, source);

        return new CodeFixTestWorkspace(workspace, solution, projectId, ids, markers);

        void AddDocument(string name, string source)
        {
            var (cleanSource, marker) = RemoveMarker(source);
            var documentId = DocumentId.CreateNewId(projectId, name);
            ids.Add(name, documentId);
            if (marker is { } span)
                markers.Add(name, span);
            solution = solution.AddDocument(documentId, name, SourceText.From(cleanSource));
        }
    }

    internal Document GetDocument(string name, Solution? solution = null)
        => (solution ?? Solution).GetDocument(_documents[name])
           ?? throw new InvalidOperationException($"Document '{name}' was not found.");

    internal async Task<Diagnostic> CreateDiagnosticAsync(
        string id,
        string documentName,
        IReadOnlyDictionary<string, string?>? properties = null)
    {
        var document = GetDocument(documentName);
        var tree = await document.GetSyntaxTreeAsync().ConfigureAwait(false)
                   ?? throw new InvalidOperationException("The test document has no syntax tree.");
        if (!_markers.TryGetValue(documentName, out var span))
            throw new InvalidOperationException($"Document '{documentName}' has no [| |] marker.");
        var descriptor = new DiagnosticDescriptor(
            id,
            id,
            "Synthetic diagnostic whose message must not control the fix",
            "Tests",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        return Diagnostic.Create(
            descriptor,
            Location.Create(tree, span),
            properties is null
                ? ImmutableDictionary<string, string?>.Empty
                : properties.ToImmutableDictionary(StringComparer.Ordinal));
    }

    internal static Diagnostic CreateLocationNoneDiagnostic(string id)
    {
        var descriptor = new DiagnosticDescriptor(
            id,
            id,
            "No source",
            "Tests",
            DiagnosticSeverity.Error,
            isEnabledByDefault: true);
        return Diagnostic.Create(descriptor, Location.None);
    }

    internal async Task<IReadOnlyList<CodeAction>> GetActionsAsync(
        Diagnostic diagnostic,
        string documentName)
        => await GetActionsAsync([diagnostic], documentName).ConfigureAwait(false);

    internal async Task<IReadOnlyList<CodeAction>> GetActionsAsync(
        ImmutableArray<Diagnostic> diagnostics,
        string documentName)
    {
        var actions = new List<CodeAction>();
        if (diagnostics.IsDefaultOrEmpty)
            return actions;
        var context = new CodeFixContext(
            GetDocument(documentName),
            diagnostics[0].Location.SourceSpan,
            diagnostics,
            (action, _) => actions.Add(action),
            CancellationToken.None);
        await CreateProvider().RegisterCodeFixesAsync(context).ConfigureAwait(false);
        return actions;
    }

    internal async Task<Solution> ApplyAsync(CodeAction action)
    {
        Solution = await GetChangedSolutionAsync(action).ConfigureAwait(false);
        return Solution;
    }

    internal static async Task<Solution> GetChangedSolutionAsync(CodeAction action)
    {
        var operations = await action.GetOperationsAsync(CancellationToken.None).ConfigureAwait(false);
        var apply = operations.OfType<ApplyChangesOperation>().SingleOrDefault()
                    ?? throw new InvalidOperationException("The code action did not provide one ApplyChangesOperation.");
        return apply.ChangedSolution;
    }

    internal async Task<string> GetTextAsync(string documentName, Solution? solution = null)
        => (await GetDocument(documentName, solution).GetTextAsync().ConfigureAwait(false)).ToString();

    internal async Task AssertCompilesAsync(Solution? solution = null)
    {
        foreach (var project in (solution ?? Solution).Projects)
        {
            var compilation = await project.GetCompilationAsync().ConfigureAwait(false)
                              ?? throw new InvalidOperationException("Compilation was unavailable.");
            var errors = compilation.GetDiagnostics()
                .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .ToArray();
            Ensure(errors.Length == 0,
                $"Expected the changed solution to compile. Actual: {FormatDiagnostics(errors)}");
        }
    }

    internal static CodeFixProvider CreateProvider()
    {
        const string assemblyName = "SharpLink.CodeFixes";
        var assembly = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(static item => item.GetName().Name == assemblyName);
        assembly ??= AssemblyLoadContext.Default.LoadFromAssemblyPath(
            Path.Combine(AppContext.BaseDirectory, assemblyName + ".dll"));
        var providerType = assembly.GetType(
            "SharpLink.CodeFixes.SharpLinkCodeFixProvider",
            throwOnError: true)!;
        return (CodeFixProvider)(Activator.CreateInstance(providerType, nonPublic: true)
               ?? throw new InvalidOperationException("Could not create the SharpLink code-fix provider."));
    }

    internal static void Ensure(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    internal static void EnsureContains(string source, string expected, string scenario)
    {
        var compactSource = Compact(source);
        var compactExpected = Compact(expected);
        Ensure(compactSource.Contains(compactExpected, StringComparison.Ordinal),
            $"Expected {scenario} to contain '{expected}'. Actual source: {source}");
    }

    internal static void EnsureDoesNotContain(string source, string unexpected, string scenario)
    {
        var compactSource = Compact(source);
        var compactUnexpected = Compact(unexpected);
        Ensure(!compactSource.Contains(compactUnexpected, StringComparison.Ordinal),
            $"Did not expect {scenario} to contain '{unexpected}'. Actual source: {source}");
    }

    internal static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics)
        => string.Join(" | ", diagnostics.Select(static item =>
            $"{item.Id}@{item.Location.GetLineSpan().StartLinePosition}: {item.GetMessage()}"));

    public void Dispose() => _workspace.Dispose();

    private static string Compact(string value)
        => new(value.Where(static character => !char.IsWhiteSpace(character)).ToArray());

    private static (string Source, TextSpan? Marker) RemoveMarker(string source)
    {
        var start = source.IndexOf(MarkerStart, StringComparison.Ordinal);
        if (start < 0)
            return (source, null);
        var end = source.IndexOf(MarkerEnd, start + MarkerStart.Length, StringComparison.Ordinal);
        if (end < 0)
            throw new InvalidOperationException("A [| marker has no matching |] marker.");
        if (source.IndexOf(MarkerStart, end + MarkerEnd.Length, StringComparison.Ordinal) >= 0)
            throw new InvalidOperationException("Each test document may contain only one diagnostic marker.");

        var markedLength = end - start - MarkerStart.Length;
        var cleaned = source.Remove(end, MarkerEnd.Length).Remove(start, MarkerStart.Length);
        return (cleaned, new TextSpan(start, markedLength));
    }

    private static IEnumerable<MetadataReference> GetPlatformReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES");
        if (string.IsNullOrWhiteSpace(tpa))
            throw new InvalidOperationException("TRUSTED_PLATFORM_ASSEMBLIES is unavailable.");
        return tpa.Split(Path.PathSeparator)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => MetadataReference.CreateFromFile(path));
    }
}

internal sealed class TestFixAllDiagnosticProvider(
    IReadOnlyDictionary<DocumentId, ImmutableArray<Diagnostic>> diagnostics)
    : FixAllContext.DiagnosticProvider
{
    public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(
        Project project,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics.Values.SelectMany(static item => item));

    public override Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(
        Document document,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<Diagnostic>>(
            diagnostics.TryGetValue(document.Id, out var value) ? value : []);

    public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(
        Project project,
        CancellationToken cancellationToken)
        => Task.FromResult<IEnumerable<Diagnostic>>([]);
}
