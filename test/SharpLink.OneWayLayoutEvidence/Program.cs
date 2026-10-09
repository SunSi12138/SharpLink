using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SharpLink.OneWayLayoutEvidence;

internal static class Program
{
    private const BindingFlags SMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
        BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static int Main(string[] args)
    {
        if (args is ["--self-test"])
            return SelfTests.Run();
        if (args.Length % 2 != 0)
            throw new ArgumentException("Usage: --assembly <OneWayEvidence.dll> --output <json> [--label A|B|C] [--source-sha <sha>] [--generated-dir <directory>]");
        var options = Enumerable.Range(0, args.Length / 2).ToDictionary(index => args[index * 2], index => args[index * 2 + 1]);
        var assemblyPath = Path.GetFullPath(options["--assembly"]);
        var context = new EvidenceLoadContext(assemblyPath);
        var fixture = context.LoadFromAssemblyPath(assemblyPath);
        var client = context.LoadFromAssemblyName(new AssemblyName("SharpLink.Client"));
        var abstractions = context.LoadFromAssemblyName(new AssemblyName("SharpLink.Abstractions"));
        var invokerType = client.GetType("SharpLink.Client.SharpLinkClient", throwOnError: true)!;
        var noStreams = abstractions.GetType("SharpLink.Abstractions.RpcNoClientStreams", throwOnError: true)!;
        var streamWriter = abstractions.GetType("SharpLink.Abstractions.IRpcClientStreamWriter", throwOnError: true)!;
        var callSites = DiscoverCallSites(fixture).ToArray();
        if (callSites.Length == 0)
            throw new InvalidOperationException("No closed generated OneWay entry calls found; verify the fixture assembly and generated proxies.");
        var specializations = callSites.Select(site => DescribeSite(site, invokerType, noStreams, streamWriter)).ToArray();
        var generatedTypes = fixture.GetTypes().Where(static type => type.FullName?.Contains("_SharpLink", StringComparison.Ordinal) == true).ToArray();
        var generatedMethods = generatedTypes.SelectMany(static type => type.GetMethods(SMethods)).ToArray();
        var generatedDirectory = options.GetValueOrDefault("--generated-dir");
        var generatedSources = generatedDirectory is null ? [] : Directory.GetFiles(generatedDirectory, "*.cs", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal).Select(path => new { path = Path.GetRelativePath(generatedDirectory, path), bytes = new FileInfo(path).Length, sha256 = HashFile(path) }).ToArray();
        var result = new
        {
            schemaVersion = 1,
            label = options.GetValueOrDefault("--label", "unspecified"),
            sourceSha = options.GetValueOrDefault("--source-sha", "unspecified"),
            runtime = RuntimeInformation.FrameworkDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            pointerBytes = IntPtr.Size,
            measurement = "Release managed metadata/IL, exact constructed generic state machines; sizeof and field offsets are runtime-managed layout, not Marshal.SizeOf or allocation bytes",
            assemblies = new[] { fixture, client, abstractions }.Select(DescribeAssembly).ToArray(),
            specializations,
            coreFootprint = DescribeCoreFootprint(invokerType),
            clientOneWayMethods = invokerType.GetMethods(SMethods).Where(static method => method.Name.Contains("OneWay", StringComparison.Ordinal))
                .OrderBy(MethodName, StringComparer.Ordinal).Select(static method => new { method = MethodName(method), il = IlReader.Describe(method), isAsync = method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>() is not null }).ToArray(),
            publicSurface = new[] { client, abstractions }.Select(DescribePublicSurface).ToArray(),
            generatedSurface = new
            {
                typeCount = generatedTypes.Length,
                methodCount = generatedMethods.Length,
                aggregateMethodIlBytes = generatedMethods.Sum(static method => method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0),
                types = generatedTypes.Select(TypeName).Order(StringComparer.Ordinal).ToArray(),
                oneWayEntries = callSites.Select(static site => MethodName(site.Entry)).Distinct().Order(StringComparer.Ordinal).ToArray(),
                sourceFiles = generatedSources,
                sourceBytes = generatedSources.Sum(static source => source.bytes)
            }
        };
        var output = Path.GetFullPath(options["--output"]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + "\n");
        Console.WriteLine($"Wrote {callSites.Length} exact generated call-site layouts to {output}");
        return 0;
    }

    private static IEnumerable<CallSite> DiscoverCallSites(Assembly assembly)
    {
        foreach (var method in assembly.GetTypes().SelectMany(static type => type.GetMethods(SMethods)))
        {
            if (method.ContainsGenericParameters)
                continue;
            foreach (var call in IlReader.ResolveCalls(method))
            {
                if (call.Callee is MethodInfo { IsGenericMethod: true, ContainsGenericParameters: false } entry &&
                    entry.Name.Contains("OneWay", StringComparison.Ordinal) && entry.Name.Contains("Invoke", StringComparison.Ordinal) &&
                    entry.DeclaringType?.Assembly != assembly)
                    yield return new CallSite(method, entry, call.Instruction.Offset);
            }
        }
    }

    private static object DescribeSite(CallSite site, Type invokerType, Type noStreams, Type streamWriter)
    {
        var arguments = site.Entry.GetGenericArguments();
        var request = arguments[0];
        var streams = arguments.FirstOrDefault(streamWriter.IsAssignableFrom) ?? noStreams;
        var streamCount = site.Proxy.GetParameters().Count(static parameter => IsStream(parameter.ParameterType));
        var closed = new List<object>();
        foreach (var method in invokerType.GetMethods(SMethods).Where(static method => method.Name.Contains("OneWay", StringComparison.Ordinal)))
        {
            if (streamCount == 0 && method.Name.Contains("StreamingCore", StringComparison.Ordinal))
                continue;
            if (streamCount > 0 && method.Name.Contains("PlainCore", StringComparison.Ordinal))
                continue;
            MethodInfo actual = method;
            if (method.IsGenericMethodDefinition)
            {
                var parameters = method.GetGenericArguments();
                if (parameters.Any(static parameter => parameter.Name is not ("TRequest" or "TStreams")))
                    continue;
                actual = method.MakeGenericMethod(parameters.Select(parameter => parameter.Name == "TRequest" ? request : streams).ToArray());
            }
            if (actual.ContainsGenericParameters)
                continue;
            closed.Add(new { method = MethodName(actual), il = IlReader.Describe(actual), asyncState = ManagedLayout.AsyncState(actual) });
        }
        return new
        {
            proxyMethod = MethodName(site.Proxy),
            proxyIl = IlReader.Describe(site.Proxy),
            entryCallOffset = site.Offset,
            generatedEntry = MethodName(site.Entry),
            streamCount,
            request = ManagedLayout.Describe(request),
            streamWriter = ManagedLayout.Describe(streams),
            streamWriterMethods = streams.GetMethods(SMethods).Where(static method => method.Name == "WriteAsync")
                .Select(static method => new { method = MethodName(method), il = IlReader.Describe(method), asyncState = ManagedLayout.AsyncState(method) }).ToArray(),
            applicableClientMethods = closed
        };
    }

    private static object DescribeCoreFootprint(Type invokerType)
    {
        var cores = invokerType.GetMethods(SMethods).Where(static method =>
            method.Name.StartsWith("InvokeOneWay", StringComparison.Ordinal) && method.Name.Contains("CoreAsync", StringComparison.Ordinal)).ToArray();
        var moveNext = cores.Select(static method => method.GetCustomAttribute<System.Runtime.CompilerServices.AsyncStateMachineAttribute>()?.StateMachineType
            .GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)).OfType<MethodInfo>().ToArray();
        return new
        {
            coreMethodCount = cores.Length,
            asyncCoreCount = moveNext.Length,
            aggregateCoreMoveNextIlBytes = moveNext.Sum(static method => method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0),
            aggregateCoreKickoffAndDispatcherIlBytes = cores.Sum(static method => method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0),
            interpretation = "Sum of method definitions, not multiplied by generic instantiations; compare aggregate growth as well as per-specialization shrinkage. Native generic sharing and inlining need separate native evidence."
        };
    }

    private static bool IsStream(Type type)
        => type.IsGenericType && type.GetGenericTypeDefinition().FullName == "System.Collections.Generic.IAsyncEnumerable`1";

    private static object DescribeAssembly(Assembly assembly)
        => new { name = assembly.GetName().Name, assembly.ManifestModule.ModuleVersionId, fileBytes = new FileInfo(assembly.Location).Length, sha256 = HashFile(assembly.Location) };

    private static object DescribePublicSurface(Assembly assembly)
    {
        var exported = assembly.GetExportedTypes();
        var methods = exported.SelectMany(static type => type.GetMethods(SMethods)
            .Where(static method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly)).ToArray();
        var signatures = exported.SelectMany(static type => new[] { TypeName(type) }
                .Concat(type.GetMembers(SMethods).Where(IsVisible).Select(member => TypeName(type) + "::" + MemberName(member))))
            .Order(StringComparer.Ordinal).ToArray();
        return new
        {
            assembly = assembly.GetName().Name,
            exportedTypeCount = exported.Length,
            methodCount = methods.Length,
            signatureCount = signatures.Length,
            sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", signatures)))),
            oneWayMethods = methods.Where(static method => method.Name.Contains("OneWay", StringComparison.Ordinal)).OrderBy(MethodName, StringComparer.Ordinal)
                .Select(static method => new { signature = MethodName(method), il = IlReader.Describe(method), isAbstract = method.IsAbstract, isVirtual = method.IsVirtual }).ToArray(),
            signatures
        };
    }

    private static bool IsVisible(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(true).Any(static method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly),
        EventInfo eventInfo => eventInfo.AddMethod?.IsPublic == true || eventInfo.AddMethod?.IsFamily == true,
        Type type => type.IsNestedPublic || type.IsNestedFamily || type.IsNestedFamORAssem,
        _ => false
    };

    private static string MemberName(MemberInfo member) => member is MethodBase method ? MethodName(method) : member.ToString() ?? member.Name;
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    internal static string MethodName(MethodBase method)
        => $"{TypeName(method.DeclaringType!)}::{method.Name}{(method.IsGenericMethod ? "<" + string.Join(",", method.GetGenericArguments().Select(TypeName)) + ">" : string.Empty)}({string.Join(",", method.GetParameters().Select(static parameter => TypeName(parameter.ParameterType)))}){(method is MethodInfo info ? ":" + TypeName(info.ReturnType) : string.Empty)}";

    internal static string TypeName(Type type)
    {
        if (type.IsByRef)
            return TypeName(type.GetElementType()!) + "&";
        if (type.IsArray)
            return TypeName(type.GetElementType()!) + "[]";
        if (type.IsGenericParameter)
            return type.Name;
        if (!type.IsGenericType)
            return type.FullName ?? type.Name;
        return (type.GetGenericTypeDefinition().FullName ?? type.Name) + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private sealed record CallSite(MethodInfo Proxy, MethodInfo Entry, int Offset);

    private sealed class EvidenceLoadContext(string path) : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var dependency = _resolver.ResolveAssemblyToPath(assemblyName);
            if (dependency is null)
            {
                var adjacent = Path.Combine(Path.GetDirectoryName(path)!, assemblyName.Name + ".dll");
                dependency = File.Exists(adjacent) ? adjacent : null;
            }
            return dependency is null ? null : LoadFromAssemblyPath(dependency);
        }
    }
}
