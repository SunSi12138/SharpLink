using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using SharpLink.Client;
using SharpLink.Server;

namespace SharpLink.Benchmarks;

internal static class LayoutEvidenceRunner
{
    private static readonly HashSet<string> STargetMethods = new(StringComparer.Ordinal)
    {
        "RunAsync",
        "RunCoreAsync",
        "ProcessRequestLoop",
        "DispatchRpcAsync",
        "DispatchOneWayRpc",
        "AwaitDispatchRpcAsync",
        "InvokeServiceTrackedAsync",
        "InvokeUnaryAsync",
        "InvokeUnaryCoreAsync",
        "InvokeUnaryWithOptionalRetryAsync",
        "InvokeUnaryWithRetryAsync",
        "InvokeUnaryRetryAttemptAsync",
        "SelectEndpoint",
        "SelectConnection"
    };

    public static async Task RunAsync(string[] args)
    {
        if (args.Length != 1)
            throw new ArgumentException("Usage: --layout-evidence <output-json>");

        var outputPath = Path.GetFullPath(args[0]);
        var assemblies = new[]
        {
            typeof(SharpLinkServerBuilder).Assembly,
            typeof(SharpClientBuilder).Assembly
        };
        var methods = new List<MethodLayoutEvidence>();
        foreach (var assembly in assemblies)
        {
            foreach (var type in assembly.GetTypes())
            {
                if (!IsTargetType(type))
                    continue;
                foreach (var method in type.GetMethods(
                             BindingFlags.Instance |
                             BindingFlags.Static |
                             BindingFlags.Public |
                             BindingFlags.NonPublic |
                             BindingFlags.DeclaredOnly))
                {
                    if (!STargetMethods.Contains(method.Name))
                        continue;
                    var stateMachine = method.GetCustomAttribute<AsyncStateMachineAttribute>()?.StateMachineType;
                    var moveNext = stateMachine?.GetMethod(
                        "MoveNext",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    methods.Add(new MethodLayoutEvidence
                    {
                        Assembly = assembly.GetName().Name ?? string.Empty,
                        DeclaringType = type.FullName ?? type.Name,
                        Method = method.Name,
                        GenericArity = method.IsGenericMethodDefinition
                            ? method.GetGenericArguments().Length
                            : 0,
                        ParameterCount = method.GetParameters().Length,
                        MethodIlBytes = GetIlBytes(method),
                        StateMachineType = stateMachine?.FullName,
                        StateMachineMoveNextIlBytes = moveNext is null ? null : GetIlBytes(moveNext)
                    });
                }
            }
        }

        var document = new LayoutEvidenceDocument
        {
            Commit = Environment.GetEnvironmentVariable("SHARPLINK_BENCHMARK_SHA") ?? "unknown",
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Assemblies = assemblies.Select(static assembly => new AssemblyLayoutEvidence
            {
                Name = assembly.GetName().Name ?? string.Empty,
                Path = assembly.Location,
                FileBytes = new FileInfo(assembly.Location).Length
            }).ToArray(),
            Methods = methods
                .OrderBy(static item => item.Assembly, StringComparer.Ordinal)
                .ThenBy(static item => item.DeclaringType, StringComparer.Ordinal)
                .ThenBy(static item => item.Method, StringComparer.Ordinal)
                .ThenBy(static item => item.ParameterCount)
                .ToArray()
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(
            outputPath,
            JsonSerializer.Serialize(document, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true
            })).ConfigureAwait(false);
    }

    private static bool IsTargetType(Type type)
        => type.FullName?.StartsWith("SharpLink.Server.SharpLinkServer", StringComparison.Ordinal) == true ||
           type.FullName?.StartsWith("SharpLink.Client.SharpLinkClient", StringComparison.Ordinal) == true;

    private static int GetIlBytes(MethodInfo method)
        => method.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0;
}

internal sealed class LayoutEvidenceDocument
{
    public string Commit { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;
    public IReadOnlyList<AssemblyLayoutEvidence> Assemblies { get; init; } = [];
    public IReadOnlyList<MethodLayoutEvidence> Methods { get; init; } = [];
}

internal sealed class AssemblyLayoutEvidence
{
    public string Name { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public long FileBytes { get; init; }
}

internal sealed class MethodLayoutEvidence
{
    public string Assembly { get; init; } = string.Empty;
    public string DeclaringType { get; init; } = string.Empty;
    public string Method { get; init; } = string.Empty;
    public int GenericArity { get; init; }
    public int ParameterCount { get; init; }
    public int MethodIlBytes { get; init; }
    public string? StateMachineType { get; init; }
    public int? StateMachineMoveNextIlBytes { get; init; }
}
