#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import math
import re
import statistics
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Iterable

KINDS = ("Unary", "OneWay", "ClientStreaming", "ServerStreaming", "DuplexStreaming")
RESPONSES = ("None", "Value", "RequiredRef", "NullableRef")
MAX_CLIENT_STREAMS = 127


@dataclass(frozen=True, order=True)
class Shape:
    kind: str
    request_payload: bool
    response: str
    client_stream_count: int
    timeout: bool
    idempotent: bool
    cancellation: bool


@dataclass(frozen=True)
class Entry:
    name: str
    shapes: tuple[Shape, ...]


REPRESENTATIVES = (
    Shape("Unary", True, "Value", 0, False, False, False),
    Shape("Unary", True, "NullableRef", 0, True, True, True),
    Shape("OneWay", True, "None", 0, False, False, False),
    Shape("OneWay", False, "None", 2, True, False, False),
    Shape("ClientStreaming", False, "Value", 1, False, False, True),
    Shape("ServerStreaming", True, "RequiredRef", 0, True, False, False),
    Shape("DuplexStreaming", False, "NullableRef", 2, True, False, True),
)


def response_values(kind: str) -> tuple[str, ...]:
    if kind == "OneWay":
        return ("None",)
    if kind in ("ServerStreaming", "DuplexStreaming"):
        return ("Value", "RequiredRef", "NullableRef")
    return RESPONSES


def stream_counts(kind: str) -> range | tuple[int, ...]:
    if kind in ("Unary", "ServerStreaming"):
        return (0,)
    if kind == "OneWay":
        return range(0, MAX_CLIENT_STREAMS + 1)
    return range(1, MAX_CLIENT_STREAMS + 1)


def reachable_shapes() -> tuple[Shape, ...]:
    values: list[Shape] = []
    for kind in KINDS:
        for request_payload in (False, True):
            for response in response_values(kind):
                for client_stream_count in stream_counts(kind):
                    for timeout in (False, True):
                        idempotent_values = (False, True) if kind == "Unary" else (False,)
                        for idempotent in idempotent_values:
                            for cancellation in (False, True):
                                values.append(
                                    Shape(
                                        kind,
                                        request_payload,
                                        response,
                                        client_stream_count,
                                        timeout,
                                        idempotent,
                                        cancellation,
                                    )
                                )
    return tuple(values)


def stream_class(count: int) -> str:
    if count == 0:
        return "0"
    if count == 1:
        return "1"
    return "many"


def group_key(variant: str, shape: Shape) -> tuple[object, ...]:
    if variant == "A":
        return (shape.kind,)
    if variant == "B":
        if shape.kind == "Unary":
            return (shape.kind, "retryable" if shape.idempotent else "plain")
        if shape.kind == "OneWay":
            return (shape.kind, "stream" if shape.client_stream_count else "no-stream")
        return (shape.kind,)
    if variant == "C":
        return (
            shape.kind,
            shape.response,
            stream_class(shape.client_stream_count),
            shape.timeout,
            shape.idempotent,
            shape.cancellation,
        )
    if variant == "D":
        return (
            shape.kind,
            shape.request_payload,
            shape.response,
            shape.client_stream_count,
            shape.timeout,
            shape.idempotent,
            shape.cancellation,
        )
    raise ValueError(f"unknown variant: {variant}")


def sanitize(value: object) -> str:
    token = str(value)
    token = token.replace("ClientStreaming", "Client")
    token = token.replace("ServerStreaming", "Server")
    token = token.replace("DuplexStreaming", "Duplex")
    token = token.replace("RequiredRef", "ReqRef")
    token = token.replace("NullableRef", "NullRef")
    token = token.replace("True", "Y").replace("False", "N")
    token = token.replace("-", "_")
    return re.sub(r"[^A-Za-z0-9_]", "_", token)


def entries_for_variant(variant: str) -> tuple[Entry, ...]:
    groups: dict[tuple[object, ...], list[Shape]] = {}
    for shape in reachable_shapes():
        groups.setdefault(group_key(variant, shape), []).append(shape)
    ordered = sorted(groups.items(), key=lambda item: repr(item[0]))
    entries: list[Entry] = []
    for index, (key, shapes) in enumerate(ordered):
        suffix = "_".join(sanitize(part) for part in key)
        name = f"Probe_{variant}_{index:05d}_{suffix}"
        entries.append(Entry(name, tuple(shapes)))
    return tuple(entries)


def exact_breakdown(shapes: Iterable[Shape]) -> dict[str, int]:
    result = {kind: 0 for kind in KINDS}
    for shape in shapes:
        result[shape.kind] += 1
    return result


def equivalent_breakdown(shapes: Iterable[Shape]) -> dict[str, int]:
    values = {
        (
            shape.kind,
            shape.request_payload,
            shape.response,
            stream_class(shape.client_stream_count),
            shape.timeout,
            shape.idempotent,
            shape.cancellation,
        )
        for shape in shapes
    }
    return {kind: sum(1 for value in values if value[0] == kind) for kind in KINDS}


def lattice_document() -> dict[str, object]:
    shapes = reachable_shapes()
    theoretical = len(KINDS) * 2 * len(RESPONSES) * (MAX_CLIENT_STREAMS + 1) * 2 * 2 * 2
    equivalent = {
        (
            shape.kind,
            shape.request_payload,
            shape.response,
            stream_class(shape.client_stream_count),
            shape.timeout,
            shape.idempotent,
            shape.cancellation,
        )
        for shape in shapes
    }
    variants = {variant: len(entries_for_variant(variant)) for variant in "ABCD"}
    return {
        "maxClientStreams": MAX_CLIENT_STREAMS,
        "currentProductionEntryPoints": 5,
        "theoreticalCartesianCount": theoretical,
        "reachableExactCount": len(shapes),
        "reachableEquivalentCount": len(equivalent),
        "exactByKind": exact_breakdown(shapes),
        "equivalentByKind": equivalent_breakdown(shapes),
        "variants": variants,
        "variantDefinitions": {
            "A": "Current five major lifecycle entry points; remaining method facts are runtime values.",
            "B": "Major lifecycle plus unary retryability and OneWay client-stream presence.",
            "C": "All control-flow/state-ownership facts specialized; request-payload presence and exact many-stream count remain dynamic.",
            "D": "Every exact reachable shape, including request-payload presence and exact client-stream count 0..127.",
        },
    }


def assert_invariants() -> None:
    document = lattice_document()
    expected = {
        "theoreticalCartesianCount": 40960,
        "reachableExactCount": 8224,
        "reachableEquivalentCount": 224,
    }
    for key, value in expected.items():
        if document[key] != value:
            raise AssertionError(f"{key}: expected {value}, got {document[key]}")
    if document["exactByKind"] != {
        "Unary": 64,
        "OneWay": 1024,
        "ClientStreaming": 4064,
        "ServerStreaming": 24,
        "DuplexStreaming": 3048,
    }:
        raise AssertionError(f"unexpected exact breakdown: {document['exactByKind']}")
    if document["equivalentByKind"] != {
        "Unary": 64,
        "OneWay": 24,
        "ClientStreaming": 64,
        "ServerStreaming": 24,
        "DuplexStreaming": 48,
    }:
        raise AssertionError(f"unexpected equivalent breakdown: {document['equivalentByKind']}")
    if document["variants"] != {"A": 5, "B": 7, "C": 112, "D": 8224}:
        raise AssertionError(f"unexpected variant counts: {document['variants']}")
    shapes = set(reachable_shapes())
    for representative in REPRESENTATIVES:
        if representative not in shapes:
            raise AssertionError(f"representative is not reachable: {representative}")


def csharp_bool(value: bool) -> str:
    return "true" if value else "false"


def csharp_shape(shape: Shape) -> str:
    return (
        "new Shape("
        f"CallKind.{shape.kind}, "
        f"{csharp_bool(shape.request_payload)}, "
        f"ResponseKind.{shape.response}, "
        f"{shape.client_stream_count}, "
        f"{csharp_bool(shape.timeout)}, "
        f"{csharp_bool(shape.idempotent)}, "
        f"{csharp_bool(shape.cancellation)})"
    )


def static_value(shapes: tuple[Shape, ...], attribute: str):
    values = {getattr(shape, attribute) for shape in shapes}
    if len(values) == 1:
        return next(iter(values))
    return None


def dynamic_parameters(entry: Entry) -> list[tuple[str, str, str]]:
    parameters: list[tuple[str, str, str]] = []
    facts = (
        ("request_payload", "bool", "requestPayload"),
        ("response", "ResponseKind", "response"),
        ("client_stream_count", "int", "clientStreamCount"),
        ("timeout", "bool", "hasTimeout"),
        ("idempotent", "bool", "isIdempotent"),
        ("cancellation", "bool", "cancellationRelevant"),
    )
    for attribute, type_name, parameter_name in facts:
        if static_value(entry.shapes, attribute) is None:
            parameters.append((attribute, type_name, parameter_name))

    cancellation = static_value(entry.shapes, "cancellation")
    if cancellation is None or cancellation:
        parameters.append(("cancellation_token", "CancellationToken", "cancellationToken"))
    return parameters


def dispatch_arguments(entry: Entry, representative_index: int) -> str:
    shape_expression = f"SRepresentatives[{representative_index}]"
    expressions = ["seed"]
    value_expressions = {
        "request_payload": f"{shape_expression}.RequestPayload",
        "response": f"{shape_expression}.Response",
        "client_stream_count": f"{shape_expression}.ClientStreamCount",
        "timeout": f"{shape_expression}.HasTimeout",
        "idempotent": f"{shape_expression}.IsIdempotent",
        "cancellation": f"{shape_expression}.CancellationRelevant",
        "cancellation_token": "cancellationToken",
    }
    for attribute, _, _ in dynamic_parameters(entry):
        expressions.append(value_expressions[attribute])
    return ", ".join(expressions)


def method_body(entry: Entry) -> str:
    request = static_value(entry.shapes, "request_payload")
    response = static_value(entry.shapes, "response")
    streams = static_value(entry.shapes, "client_stream_count")
    timeout = static_value(entry.shapes, "timeout")
    idempotent = static_value(entry.shapes, "idempotent")
    cancellation = static_value(entry.shapes, "cancellation")

    parameters = [("seed", "int", "seed")]
    parameters.extend(dynamic_parameters(entry))
    signature = ", ".join(f"{type_name} {name}" for _, type_name, name in parameters)
    lines = [
        f"    private static async ValueTask<int> {entry.name}({signature})",
        "    {",
        "        var score = seed;",
        "        await Task.Yield();",
    ]

    if request is None:
        lines += [
            "        if (requestPayload)",
            "            score += 3;",
        ]
    elif request:
        lines.append("        score += 3;")

    if response is None:
        lines += [
            "        score += response switch",
            "        {",
            "            ResponseKind.None => 0,",
            "            ResponseKind.Value => 5,",
            "            ResponseKind.RequiredRef => 7,",
            "            ResponseKind.NullableRef => 9,",
            "            _ => throw new ArgumentOutOfRangeException()",
            "        };",
        ]
    else:
        response_scores = {"None": 0, "Value": 5, "RequiredRef": 7, "NullableRef": 9}
        if response_scores[response]:
            lines.append(f"        score += {response_scores[response]};")

    if streams is None:
        lines += [
            "        if (clientStreamCount == 1)",
            "            score += 11;",
            "        else if (clientStreamCount > 1)",
            "            score += 17 + (clientStreamCount & 7);",
        ]
    elif streams == 1:
        lines.append("        score += 11;")
    elif streams > 1:
        lines.append(f"        score += {17 + (streams & 7)};")

    if timeout is None:
        lines += [
            "        if (hasTimeout)",
            "            score += 23;",
        ]
    elif timeout:
        lines.append("        score += 23;")

    if idempotent is None:
        lines += [
            "        if (isIdempotent)",
            "            score += 29;",
        ]
    elif idempotent:
        lines.append("        score += 29;")

    if cancellation is None:
        lines += [
            "        if (cancellationRelevant && cancellationToken.CanBeCanceled)",
            "            score += 31;",
        ]
    elif cancellation:
        lines += [
            "        if (cancellationToken.CanBeCanceled)",
            "            score += 31;",
        ]

    lines += [
        "        return score;",
        "    }",
    ]
    return "\n".join(lines)


def generate_probe(variant: str, output_dir: Path) -> None:
    assert_invariants()
    output_dir.mkdir(parents=True, exist_ok=True)
    entries = entries_for_variant(variant)
    entry_by_group = {group_key(variant, entry.shapes[0]): index for index, entry in enumerate(entries)}
    rep_entry_indexes = [entry_by_group[group_key(variant, shape)] for shape in REPRESENTATIVES]
    rep_method_names = [entries[index].name for index in rep_entry_indexes]

    csproj = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <AssemblyName>ShapeProbe</AssemblyName>
    <RootNamespace>ShapeProbe</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <TrimmerRootAssembly Include="$(AssemblyName)" />
  </ItemGroup>
</Project>
"""
    (output_dir / "ShapeProbe.csproj").write_text(csproj, encoding="utf-8")

    dispatch_cases = []
    for representative_index, entry_index in enumerate(rep_entry_indexes):
        entry = entries[entry_index]
        arguments = dispatch_arguments(entry, representative_index)
        dispatch_cases.append(
            f"            {representative_index} => {entry.name}({arguments}),"
        )

    source_parts = [
        "using System;",
        "using System.Diagnostics;",
        "using System.Globalization;",
        "using System.IO;",
        "using System.Threading;",
        "using System.Threading.Tasks;",
        "",
        "namespace ShapeProbe;",
        "",
        "internal enum CallKind",
        "{",
        "    Unary,",
        "    OneWay,",
        "    ClientStreaming,",
        "    ServerStreaming,",
        "    DuplexStreaming",
        "}",
        "",
        "internal enum ResponseKind",
        "{",
        "    None,",
        "    Value,",
        "    RequiredRef,",
        "    NullableRef",
        "}",
        "",
        "internal readonly record struct Shape(",
        "    CallKind Kind,",
        "    bool RequestPayload,",
        "    ResponseKind Response,",
        "    int ClientStreamCount,",
        "    bool HasTimeout,",
        "    bool IsIdempotent,",
        "    bool CancellationRelevant);",
        "",
        "internal static class Program",
        "{",
        f'    private const string Variant = "{variant}";',
        f"    private const int MethodCount = {len(entries)};",
        "    private static readonly Shape[] SRepresentatives =",
        "    [",
    ]
    source_parts += [f"        {csharp_shape(shape)}," for shape in REPRESENTATIVES]
    source_parts += [
        "    ];",
        "",
        "    public static async Task<int> Main(string[] args)",
        "    {",
        "        if (args.Length != 2)",
        "            throw new ArgumentException(\"Usage: ShapeProbe <iterations> <output-json>\");",
        "        var iterations = int.Parse(args[0], CultureInfo.InvariantCulture);",
        "        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(iterations);",
        "        var outputPath = Path.GetFullPath(args[1]);",
        "        using var cancellation = new CancellationTokenSource();",
        "        var token = cancellation.Token;",
        "        long checksum = 0;",
        "        var warmup = Math.Min(iterations, 4096);",
        "        for (var index = 0; index < warmup; index++)",
        "            checksum += await DispatchRepresentative(index % SRepresentatives.Length, index, token).ConfigureAwait(false);",
        "        GC.Collect();",
        "        GC.WaitForPendingFinalizers();",
        "        GC.Collect();",
        "        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);",
        "        var started = Stopwatch.GetTimestamp();",
        "        for (var index = 0; index < iterations; index++)",
        "            checksum += await DispatchRepresentative(index % SRepresentatives.Length, index, token).ConfigureAwait(false);",
        "        var elapsed = Stopwatch.GetElapsedTime(started);",
        "        var allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);",
        "        var nsPerOperation = elapsed.TotalNanoseconds / iterations;",
        "        var allocatedPerOperation = (allocatedAfter - allocatedBefore) / (double)iterations;",
        "        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);",
        "        var json = FormattableString.Invariant(",
        '            $"{{\\\"variant\\\":\\\"{Variant}\\\",\\\"methodCount\\\":{MethodCount},\\\"operations\\\":{iterations},\\\"nanosecondsPerOperation\\\":{nsPerOperation:R},\\\"allocatedBytesPerOperation\\\":{allocatedPerOperation:R},\\\"checksum\\\":{checksum}}}");',
        "        await File.WriteAllTextAsync(outputPath, json + Environment.NewLine).ConfigureAwait(false);",
        "        Console.WriteLine(json);",
        "        return 0;",
        "    }",
        "",
        "    private static ValueTask<int> DispatchRepresentative(",
        "        int representative,",
        "        int seed,",
        "        CancellationToken cancellationToken)",
        "        => representative switch",
        "        {",
    ]
    source_parts += dispatch_cases
    source_parts += [
        "            _ => throw new ArgumentOutOfRangeException(nameof(representative))",
        "        };",
        "",
    ]
    for entry in entries:
        source_parts.append(method_body(entry))
        source_parts.append("")
    source_parts.append("}")
    source = "\n".join(source_parts) + "\n"
    (output_dir / "Program.cs").write_text(source, encoding="utf-8")
    (output_dir / "representatives.txt").write_text(
        "\n".join(rep_method_names) + "\n",
        encoding="utf-8",
    )
    manifest = {
        "variant": variant,
        "methodCount": len(entries),
        "sourceLines": source.count("\n"),
        "sourceBytes": len(source.encode("utf-8")),
        "representativeMethods": rep_method_names,
        "representativeShapes": [asdict(shape) for shape in REPRESENTATIVES],
    }
    (output_dir / "manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n",
        encoding="utf-8",
    )


def generate_inspector(output_dir: Path) -> None:
    output_dir.mkdir(parents=True, exist_ok=True)
    csproj = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
"""
    source = r'''using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Inspector;

internal static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length != 3)
            throw new ArgumentException("Usage: Inspector <assembly> <representatives.txt> <output-json>");

        var assembly = Assembly.LoadFrom(Path.GetFullPath(args[0]));
        var host = assembly.GetType("ShapeProbe.Program", throwOnError: true)!;
        var names = File.ReadAllLines(args[1])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();
        var rows = new List<object>();
        foreach (var name in names)
        {
            var stateMachine = host.GetNestedTypes(BindingFlags.NonPublic)
                .Single(type => type.Name.StartsWith($"<{name}>d__", StringComparison.Ordinal));
            var fields = stateMachine.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var moveNext = stateMachine.GetMethod(
                "MoveNext",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!;
            var stub = host.GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic)!;
            rows.Add(new
            {
                method = name,
                stateMachineSizeBytes = SizeOf(stateMachine),
                stateMachineFieldCount = fields.Length,
                stateMachinePayloadFieldCount = fields.Count(static field =>
                    field.Name is not "<>1__state" && field.Name is not "<>t__builder"),
                fields = fields.Select(static field => new
                {
                    field.Name,
                    type = field.FieldType.FullName ?? field.FieldType.Name
                }).ToArray(),
                stubIlBytes = stub.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0,
                moveNextIlBytes = moveNext.GetMethodBody()?.GetILAsByteArray()?.Length ?? 0
            });
        }

        var output = new
        {
            assembly = Path.GetFileName(args[0]),
            methods = rows
        };
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[2]))!);
        File.WriteAllText(
            args[2],
            JsonSerializer.Serialize(output, options) + Environment.NewLine);
        return 0;
    }

    private static int SizeOf(Type type)
    {
        var method = typeof(Program).GetMethod(
            nameof(SizeOfGeneric),
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(type);
        return (int)method.Invoke(null, null)!;
    }

    private static int SizeOfGeneric<T>() => Unsafe.SizeOf<T>();
}
'''
    (output_dir / "Inspector.csproj").write_text(csproj, encoding="utf-8")
    (output_dir / "Program.cs").write_text(source, encoding="utf-8")


def inventory_generated(generated_dir: Path, output: Path) -> None:
    if not generated_dir.is_dir():
        raise FileNotFoundError(f"generated source directory does not exist: {generated_dir}")

    entrypoints = (
        "InvokeUnaryAsync",
        "InvokeOneWayAsync",
        "InvokeClientStreamingAsync",
        "InvokeServerStreamingAsync",
        "InvokeDuplexStreamingAsync",
    )
    files = sorted(generated_dir.rglob("*.cs"))
    combined_parts: list[str] = []
    total_lines = 0
    total_bytes = 0
    for path in files:
        content = path.read_text(encoding="utf-8")
        combined_parts.append(content)
        total_lines += content.count("\n")
        total_bytes += len(content.encode("utf-8"))
    combined = "\n".join(combined_parts)

    by_entrypoint: dict[str, dict[str, object]] = {}
    all_closed: set[str] = set()
    for entrypoint in entrypoints:
        token = entrypoint + "<"
        position = 0
        closed: list[str] = []
        while True:
            start = combined.find(token, position)
            if start < 0:
                break
            angle = start + len(entrypoint)
            depth = 0
            end = angle
            while end < len(combined):
                character = combined[end]
                if character == "<":
                    depth += 1
                elif character == ">":
                    depth -= 1
                    if depth == 0:
                        end += 1
                        break
                end += 1
            if depth != 0:
                raise ValueError(f"unterminated generic argument list after {entrypoint}")
            generic = " ".join(combined[angle:end].split())
            spelling = entrypoint + generic
            closed.append(spelling)
            all_closed.add(spelling)
            position = end

        by_entrypoint[entrypoint] = {
            "callSites": len(closed),
            "uniqueClosedGenericCallSites": len(set(closed)),
        }

    document = {
        "generatedDirectory": str(generated_dir),
        "files": len(files),
        "lines": total_lines,
        "bytes": total_bytes,
        "clientEntrypointCallSites": sum(
            int(value["callSites"]) for value in by_entrypoint.values()
        ),
        "uniqueClosedGenericClientEntrypoints": len(all_closed),
        "byEntrypoint": by_entrypoint,
        "note": (
            "This is the count of distinct closed generic Invoke*Async spellings emitted by "
            "the source generator. CLR/JIT canonical generic sharing can reduce the number of "
            "native instantiations, so this is an emitted-callsite count rather than a JIT claim."
        ),
    }
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, indent=2) + "\n", encoding="utf-8")


def parse_jit_sizes(path: Path) -> dict[str, int]:
    if not path.exists():
        return {}
    current: str | None = None
    sizes: dict[str, int] = {}
    header = re.compile(r"Assembly listing for method .*<(?P<name>Probe_[^>]+)>.*:MoveNext")
    size = re.compile(r"Total bytes of code\s+(?P<size>\d+)")
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        match = header.search(line)
        if match:
            current = match.group("name")
            continue
        match = size.search(line)
        if current is not None and match:
            sizes[current] = int(match.group("size"))
            current = None
    return sizes


def median(values: Iterable[float]) -> float:
    data = list(values)
    return statistics.median(data) if data else math.nan


def load_json(path: Path):
    return json.loads(path.read_text(encoding="utf-8"))


def format_pct(value: float) -> str:
    return f"{value:+.2f}%"


def pct_delta(value: float, baseline: float) -> float:
    if baseline == 0:
        return 0.0
    return (value / baseline - 1.0) * 100.0


def summarize(root: Path, output_markdown: Path, output_json: Path) -> None:
    lattice = load_json(root / "lattice.json")
    variants: dict[str, dict[str, object]] = {}
    for variant in "ABCD":
        directory = root / "probe" / variant
        manifest = load_json(directory / "manifest.json")
        jit_off = load_json(directory / "jit-pgo-off.json")
        jit_on = load_json(directory / "jit-pgo-on.json")
        aot = load_json(directory / "aot-run.json")
        inspector = load_json(directory / "inspector.json")
        jit_sizes = parse_jit_sizes(directory / "jit-disasm.txt")
        aot_executable = directory / "aot" / "ShapeProbe"
        aot_seconds = float((directory / "aot-build-seconds.txt").read_text().strip())
        jit_dll = directory / "jit" / "ShapeProbe.dll"
        payload_fields = [
            method["stateMachinePayloadFieldCount"] for method in inspector["methods"]
        ]
        state_sizes = [method["stateMachineSizeBytes"] for method in inspector["methods"]]
        move_next_il = [method["moveNextIlBytes"] for method in inspector["methods"]]
        native_sizes = [
            jit_sizes[name]
            for name in manifest["representativeMethods"]
            if name in jit_sizes
        ]
        variants[variant] = {
            "methodCount": manifest["methodCount"],
            "sourceLines": manifest["sourceLines"],
            "sourceBytes": manifest["sourceBytes"],
            "jitDllBytes": jit_dll.stat().st_size,
            "jitPgoOffNs": jit_off["nanosecondsPerOperation"],
            "jitPgoOnNs": jit_on["nanosecondsPerOperation"],
            "jitPgoOffAllocatedBytes": jit_off["allocatedBytesPerOperation"],
            "jitPgoOnAllocatedBytes": jit_on["allocatedBytesPerOperation"],
            "aotNs": aot["nanosecondsPerOperation"],
            "aotAllocatedBytes": aot["allocatedBytesPerOperation"],
            "aotExecutableBytes": aot_executable.stat().st_size,
            "aotBuildSeconds": aot_seconds,
            "medianStateMachinePayloadFields": median(payload_fields),
            "medianStateMachineSizeBytes": median(state_sizes),
            "medianMoveNextIlBytes": median(move_next_il),
            "medianJitMoveNextNativeBytes": median(native_sizes),
            "jitRepresentativeMethodsWithNativeSize": len(native_sizes),
        }

    full_rpc: dict[str, list[dict[str, object]]] = {}
    for mode in ("pgo-off", "pgo-on"):
        grouped: dict[str, list[dict[str, object]]] = {}
        directory = root / "full-rpc" / mode
        for path in sorted(directory.glob("*.json")):
            row = load_json(path)
            grouped.setdefault(row["scenario"], []).append(row)

        rows: list[dict[str, object]] = []
        for scenario, samples in sorted(grouped.items()):
            first = samples[0]
            rows.append({
                "scenario": scenario,
                "shape": first["shape"],
                "factSummary": first["factSummary"],
                "payloadClass": first["payloadClass"],
                "sampleCount": len(samples),
                "throughputPerSecond": median(
                    sample["throughputPerSecond"] for sample in samples
                ),
                "p50Us": median(sample["p50Us"] for sample in samples),
                "p99Us": median(sample["p99Us"] for sample in samples),
                "cpuUsPerOperation": median(
                    sample["cpuUsPerOperation"] for sample in samples
                ),
                "allocatedBytesPerOperation": median(
                    sample["allocatedBytesPerOperation"] for sample in samples
                ),
            })
        full_rpc[mode] = rows

    baseline = variants["A"]
    comparisons: dict[str, dict[str, float]] = {}
    for variant in "BCD":
        row = variants[variant]
        comparisons[variant] = {
            "jitPgoOffNsDeltaPct": pct_delta(row["jitPgoOffNs"], baseline["jitPgoOffNs"]),
            "jitPgoOnNsDeltaPct": pct_delta(row["jitPgoOnNs"], baseline["jitPgoOnNs"]),
            "aotNsDeltaPct": pct_delta(row["aotNs"], baseline["aotNs"]),
            "jitDllDeltaPct": pct_delta(row["jitDllBytes"], baseline["jitDllBytes"]),
            "aotExecutableDeltaPct": pct_delta(
                row["aotExecutableBytes"], baseline["aotExecutableBytes"]
            ),
            "aotBuildTimeDeltaPct": pct_delta(
                row["aotBuildSeconds"], baseline["aotBuildSeconds"]
            ),
        }

    d = comparisons["D"]
    full_d_speedup = max(
        -d["jitPgoOffNsDeltaPct"],
        -d["jitPgoOnNsDeltaPct"],
        -d["aotNsDeltaPct"],
    )
    full_d_cost = max(d["aotExecutableDeltaPct"], d["aotBuildTimeDeltaPct"])
    if full_d_speedup >= 5.0 and full_d_cost <= 20.0:
        full_d_decision = (
            "GO candidate: full de-fold clears the predeclared prototype benefit/cost gate; "
            "production integration still needs a real-runtime A/B."
        )
    else:
        full_d_decision = (
            "NO-GO for full de-fold: the best prototype speedup is "
            f"{full_d_speedup:.2f}% and the larger AOT image/build cost is "
            f"{full_d_cost:.2f}% (gate: >=5% speedup and <=20% AOT cost)."
        )

    c = comparisons["C"]
    selective_speedup = max(
        -c["jitPgoOffNsDeltaPct"],
        -c["jitPgoOnNsDeltaPct"],
        -c["aotNsDeltaPct"],
    )
    if selective_speedup >= 3.0 and c["aotExecutableDeltaPct"] <= 10.0:
        selective_decision = "Selective C clears the prototype gate; validate only the profitable dimensions in production."
    else:
        selective_decision = "Selective C does not clear the 3% / +10% AOT prototype gate; keep the five-entry ABI as the default and require dimension-specific evidence."

    result = {
        "lattice": lattice,
        "variants": variants,
        "comparisonsVsA": comparisons,
        "fullRpc": full_rpc,
        "decision": {
            "fullD": full_d_decision,
            "selectiveC": selective_decision,
            "gates": {
                "fullDMinSpeedupPct": 5.0,
                "fullDMaxAotCostPct": 20.0,
                "selectiveCMinSpeedupPct": 3.0,
                "selectiveCMaxAotImagePct": 10.0,
            },
        },
    }

    lines = [
        "# Client call-shape de-folding evidence",
        "",
        f"- exact reachable shapes: **{lattice['reachableExactCount']:,}**; equivalent lifecycle shapes: **{lattice['reachableEquivalentCount']:,}**",
        f"- production entry points: **{lattice['currentProductionEntryPoints']}**",
        f"- A/B/C/D prototype entries: **{lattice['variants']['A']} / {lattice['variants']['B']} / {lattice['variants']['C']} / {lattice['variants']['D']:,}**",
        "",
        "## Prototype codegen / cost",
        "",
        "| Variant | Entries | Source LOC | JIT DLL KiB | Payload fields | State size B | MoveNext IL B | JIT native B | PGO off ns | PGO on ns | AOT ns | AOT image KiB | AOT build s |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for variant in "ABCD":
        row = variants[variant]
        lines.append(
            f"| {variant} | {row['methodCount']:,} | {row['sourceLines']:,} | "
            f"{row['jitDllBytes'] / 1024:.1f} | "
            f"{row['medianStateMachinePayloadFields']:.1f} | "
            f"{row['medianStateMachineSizeBytes']:.1f} | "
            f"{row['medianMoveNextIlBytes']:.1f} | "
            f"{row['medianJitMoveNextNativeBytes']:.1f} | "
            f"{row['jitPgoOffNs']:.1f} | {row['jitPgoOnNs']:.1f} | {row['aotNs']:.1f} | "
            f"{row['aotExecutableBytes'] / 1024:.1f} | {row['aotBuildSeconds']:.1f} |"
        )
    lines += [
        "",
        "### Relative to A",
        "",
        "| Variant | PGO off | PGO on | NativeAOT | JIT DLL | AOT image | AOT build |",
        "| --- | ---: | ---: | ---: | ---: | ---: | ---: |",
    ]
    for variant in "BCD":
        row = comparisons[variant]
        lines.append(
            f"| {variant} | {format_pct(row['jitPgoOffNsDeltaPct'])} | "
            f"{format_pct(row['jitPgoOnNsDeltaPct'])} | {format_pct(row['aotNsDeltaPct'])} | "
            f"{format_pct(row['jitDllDeltaPct'])} | {format_pct(row['aotExecutableDeltaPct'])} | "
            f"{format_pct(row['aotBuildTimeDeltaPct'])} |"
        )

    lines += [
        "",
        "## Full SharpLink RPC matrix",
        "",
        "Each row is one in-process TCP client/server process. OneWay records the public local send-completion boundary; response calls record end-to-end completion.",
        "",
        "| Mode | Scenario | Shape | Payload | QPS | P50 us | P99 us | CPU us/op | B/op |",
        "| --- | --- | --- | --- | ---: | ---: | ---: | ---: | ---: |",
    ]
    for mode in ("pgo-off", "pgo-on"):
        for row in full_rpc[mode]:
            lines.append(
                f"| {mode} | {row['scenario']} | {row['shape']} | {row['payloadClass']} | "
                f"{row['throughputPerSecond']:.0f} | {row['p50Us']:.2f} | {row['p99Us']:.2f} | "
                f"{row['cpuUsPerOperation']:.2f} | {row['allocatedBytesPerOperation']:.1f} |"
            )

    lines += [
        "",
        "## Decision",
        "",
        f"- Full D: {full_d_decision}",
        f"- Selective C: {selective_decision}",
        "- The isolated prototype is an attribution/upper-bound experiment, not a production-runtime speedup claim. The full-RPC matrix anchors the scale of real calls; any production de-fold still requires same-machine base/head evidence for the specific dimension.",
        "- No shipping ABI, wire format, or runtime entry point is changed by this experiment.",
        "",
    ]
    output_markdown.parent.mkdir(parents=True, exist_ok=True)
    output_json.parent.mkdir(parents=True, exist_ok=True)
    output_markdown.write_text("\n".join(lines), encoding="utf-8")
    output_json.write_text(json.dumps(result, indent=2) + "\n", encoding="utf-8")


def main() -> int:
    parser = argparse.ArgumentParser()
    subparsers = parser.add_subparsers(dest="command", required=True)

    subparsers.add_parser("self-test")

    lattice_parser = subparsers.add_parser("lattice")
    lattice_parser.add_argument("output", type=Path)

    generate_parser = subparsers.add_parser("generate")
    generate_parser.add_argument("variant", choices=list("ABCD"))
    generate_parser.add_argument("output_dir", type=Path)

    inspector_parser = subparsers.add_parser("generate-inspector")
    inspector_parser.add_argument("output_dir", type=Path)

    inventory_parser = subparsers.add_parser("generated-inventory")
    inventory_parser.add_argument("generated_dir", type=Path)
    inventory_parser.add_argument("output", type=Path)

    summarize_parser = subparsers.add_parser("summarize")
    summarize_parser.add_argument("root", type=Path)
    summarize_parser.add_argument("output_markdown", type=Path)
    summarize_parser.add_argument("output_json", type=Path)

    args = parser.parse_args()
    if args.command == "self-test":
        assert_invariants()
        print(json.dumps(lattice_document(), indent=2))
        return 0
    if args.command == "lattice":
        assert_invariants()
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(json.dumps(lattice_document(), indent=2) + "\n", encoding="utf-8")
        return 0
    if args.command == "generate":
        generate_probe(args.variant, args.output_dir)
        return 0
    if args.command == "generate-inspector":
        generate_inspector(args.output_dir)
        return 0
    if args.command == "generated-inventory":
        inventory_generated(args.generated_dir, args.output)
        return 0
    if args.command == "summarize":
        summarize(args.root, args.output_markdown, args.output_json)
        return 0
    raise AssertionError(args.command)


if __name__ == "__main__":
    raise SystemExit(main())
