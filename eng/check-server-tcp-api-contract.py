#!/usr/bin/env python3
"""External consumer compilation gate for 3.0's TCP, Builder and logger APIs.

This compiles a fresh project *outside* the repository against the real production
projects. The negative compilations must fail for the removed Server overloads;
the positive compilation must continue to accept Server IPAddress, Client string
IP and standalone RuntimeContext. Both builders must remain publicly constructible
and fluent, while external subclasses must fail specifically with CS0509.
Explicit logger configuration and Hosting registration remain public, while each
Host-only logger fallback must be inaccessible to the external consumer.
No runtime socket or benchmark workload runs.
"""

from pathlib import Path
import re
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]

CLIENT = ROOT / "src/SharpLink.Client/SharpLink.Client.csproj"
SERVER = ROOT / "src/SharpLink.Server/SharpLink.Server.csproj"
RUNTIME = ROOT / "src/SharpLink.Runtime/SharpLink.Runtime.csproj"
HOSTING = ROOT / "src/SharpLink.Hosting/SharpLink.Hosting.csproj"
SERVER_SOURCE = ROOT / "src/SharpLink.Server/TransportExtensions.cs"
CLIENT_SOURCE = ROOT / "src/SharpLink.Client/TransportExtensions.cs"
SERVER_BASELINE = ROOT / "eng/public-api/3.0.0/SharpLink.Server.api.txt"

PREFIX = """
using System.Net;
using System.Net.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SharpLink.Client;
using SharpLink.Hosting;
using SharpLink.Runtime;
using SharpLink.Server;
public static class ConsumerProbe
{
    public static void Probe()
    {
"""
SUFFIX = """
    }
}
"""

POSITIVE = """
        _ = SharpLinkServerBuilder.Create().UseTcp(0, IPAddress.Loopback);
        _ = SharpLinkServerBuilder.Create().UseTcp(
            0, new SslServerAuthenticationOptions(), IPAddress.Loopback,
            tlsHandshakeTimeout: System.TimeSpan.FromSeconds(2));
        _ = SharpClientBuilder.Create().UseTcp("127.0.0.1", 19090);
        _ = SharpClientBuilder.Create().UseTcp(
            "127.0.0.1", 19090, new SslClientAuthenticationOptions());
        _ = new SharpClientBuilder().UseRequestTimeout().UseTcp("127.0.0.1", 19090);
        _ = new SharpLinkServerBuilder().UseTcp(0, IPAddress.Loopback);
        _ = (ISharpLinkServerBuilder)SharpLinkServerBuilder.Create();
        using var runtime = new SharpLinkRuntimeContextBuilder().Build();
        _ = runtime.Codecs.GetCodec<int>();
        _ = SharpClientBuilder.Create().UseLoggerFactory(NullLoggerFactory.Instance);
        _ = SharpLinkServerBuilder.Create().UseLoggerFactory(NullLoggerFactory.Instance);
        _ = SharpLinkMultiClusterClientBuilder.Create().UseLoggerFactory(NullLoggerFactory.Instance);
        var services = new ServiceCollection();
        _ = services.AddSharpLinkClient(builder => builder.UseLoggerFactory(NullLoggerFactory.Instance));
        _ = services.AddSharpLinkServer(builder => builder.UseLoggerFactory(NullLoggerFactory.Instance));
        _ = services.AddSharpLinkMultiClusterClient(builder => builder.UseLoggerFactory(NullLoggerFactory.Instance));
"""
NEGATIVE_PLAIN = '        _ = SharpLinkServerBuilder.Create().UseTcp(0, "127.0.0.1");'
NEGATIVE_TLS = """
        _ = SharpLinkServerBuilder.Create().UseTcp(
            0, new SslServerAuthenticationOptions(), "127.0.0.1");
"""


def run(*command: str, cwd: Path) -> subprocess.CompletedProcess[str]:
    return subprocess.run(command, cwd=cwd, text=True, capture_output=True, timeout=420)


def verify_static_contract() -> None:
    server = SERVER_SOURCE.read_text(encoding="utf-8")
    client = CLIENT_SOURCE.read_text(encoding="utf-8")
    baseline = SERVER_BASELINE.read_text(encoding="utf-8")
    assert "UseTcp(int port, string ip" not in server, "Legacy plain Server method was restored"
    assert "SslServerAuthenticationOptions tlsOptions,\n            string ip" not in server, (
        "Legacy TLS Server method was restored"
    )
    assert "UseTcp(int port, string ip" not in baseline, "3.0 Server baseline restored legacy API"
    assert "SslServerAuthenticationOptions tlsOptions, string ip" not in baseline, (
        "3.0 Server TLS API baseline restored legacy API"
    )
    assert "UseTcp(int port, IPAddress address" in server, "Typed Server method disappeared"
    assert "public SharpClientBuilder UseTcp(string ip, int port)" in client, (
        "The independent Client string-IP API must remain"
    )



def verify_no_legacy_server_calls_in_checkout() -> None:
    """Exhaustively scan checked-in C# sources, including files absent from code-search indexing.

    This lexical check complements the actual positive/negative compiler probes.
    """
    import re

    matcher = re.compile(r"\.UseTcp\s*\(")
    for source_file in ROOT.rglob("*.cs"):
        if set(source_file.parts) & {"bin", "obj", ".git"}:
            continue
        source = source_file.read_text(encoding="utf-8-sig")
        for call in matcher.finditer(source):
            depth = 1
            braces = brackets = 0
            quoted = ""
            argument_start = call.end()
            arguments = []
            at = call.end()
            while at < len(source):
                ch = source[at]
                previous = source[at - 1] if at else ""
                if quoted:
                    if ch == quoted and previous != "\\":
                        quoted = ""
                    at += 1
                    continue
                if ch in ("'", '"'):
                    quoted = ch
                elif ch == "(":
                    depth += 1
                elif ch == ")":
                    depth -= 1
                    if depth == 0:
                        arguments.append(source[argument_start:at].strip())
                        break
                elif ch == "{":
                    braces += 1
                elif ch == "}":
                    braces -= 1
                elif ch == "[":
                    brackets += 1
                elif ch == "]":
                    brackets -= 1
                elif ch == "," and depth == 1 and braces == 0 and brackets == 0:
                    arguments.append(source[argument_start:at].strip())
                    argument_start = at + 1
                at += 1
            for index in (1, 2):
                if index >= len(arguments):
                    continue
                value = arguments[index]
                if (
                    (value.startswith('"') and value.endswith('"'))
                    or value.endswith(".ToString()")
                    or (value.startswith("ip:") and '"' in value)
                ):
                    lineno = source.count("\n", 0, call.start()) + 1
                    raise AssertionError(
                        f"Legacy string TCP binding argument at {source_file.relative_to(ROOT)}:"
                        f"{lineno}: {value}. Use IPAddress or IPAddress.Parse explicitly."
                    )


def compile_project(code: str, path: Path, *, declarations: str = "") -> subprocess.CompletedProcess[str]:
    (path / "Probe.cs").write_text(PREFIX + code + SUFFIX + declarations, encoding="utf-8")
    return run("dotnet", "build", str(path / "Consumer.csproj"),
               "-c", "Release", "--no-restore", "--verbosity", "minimal", cwd=path)


def verify_sealed_builders(folder: Path) -> None:
    for builder in ("SharpClientBuilder", "SharpLinkServerBuilder"):
        rejected = compile_project(
            "", folder, declarations=f"\npublic class ExternalSubclass : {builder} {{ }}\n"
        )
        details = rejected.stdout + rejected.stderr
        errors = re.findall(r"(?m)^.*\berror\s+[A-Z]+\d+.*$", details)
        if rejected.returncode == 0 or not errors or any(
            not re.search(r"Probe\.cs\(\d+,\d+\): error CS0509:", error)
            or "ExternalSubclass" not in error
            or builder not in error
            for error in errors
        ):
            raise RuntimeError(
                f"Expected external inheritance of {builder} to fail only with CS0509:\n"
                + details
            )
        print(f"PASS: external {builder} inheritance rejected with CS0509")


def verify_host_only_logger_fallbacks(folder: Path) -> None:
    for builder in ("SharpClientBuilder", "SharpLinkServerBuilder", "SharpLinkMultiClusterClientBuilder"):
        rejected = compile_project(
            f"        {builder}.Create().UseLoggerFactoryIfUnset(NullLoggerFactory.Instance);", folder
        )
        details = rejected.stdout + rejected.stderr
        errors = re.findall(r"(?m)^.*\berror\s+[A-Z]+\d+.*$", details)
        if rejected.returncode == 0 or not errors or any(
            not re.search(r"Probe\.cs\(\d+,\d+\): error CS(?:1061|0122):", error)
            or "UseLoggerFactoryIfUnset" not in error
            or builder not in error
            for error in errors
        ):
            raise RuntimeError(
                f"Expected external {builder} logger fallback access to fail only with CS1061/CS0122:\n"
                + details
            )
        print(f"PASS: external {builder}.UseLoggerFactoryIfUnset access rejected")


def main() -> None:
    verify_static_contract()
    verify_no_legacy_server_calls_in_checkout()
    with tempfile.TemporaryDirectory(prefix="sharplink-external-tcp-") as location:
        folder = Path(location).resolve()
        (folder / "global.json").write_bytes((ROOT / "global.json").read_bytes())
        (folder / "Consumer.csproj").write_text(
            '<Project Sdk="Microsoft.NET.Sdk">\n'
            '  <PropertyGroup>\n'
            '    <TargetFramework>net10.0</TargetFramework>\n'
            '    <Nullable>enable</Nullable>\n'
            '    <ImplicitUsings>enable</ImplicitUsings>\n'
            '  </PropertyGroup>\n'
            '  <ItemGroup>\n'
            f'    <ProjectReference Include="{CLIENT}" />\n'
            f'    <ProjectReference Include="{SERVER}" />\n'
            f'    <ProjectReference Include="{RUNTIME}" />\n'
            f'    <ProjectReference Include="{HOSTING}" />\n'
            '  </ItemGroup>\n'
            '</Project>\n',
            encoding="utf-8",
        )
        restored = run("dotnet", "restore", str(folder / "Consumer.csproj"),
                       "--verbosity", "minimal", cwd=folder)
        if restored.returncode:
            raise RuntimeError("External consumer restore failed:\n" + restored.stdout + restored.stderr)

        passed = compile_project(POSITIVE, folder)
        if passed.returncode:
            raise RuntimeError("Typed Server, Client and standalone Context API compile failed:\n"
                               + passed.stdout + passed.stderr)
        print("PASS: positive Server typed TLS/plain, Client string IP, standalone Context")
        print("PASS: public Client/Server/MultiCluster logger configuration and Hosting registration")

        for name, source in (("plain", NEGATIVE_PLAIN), ("tls", NEGATIVE_TLS)):
            rejected = compile_project(source, folder)
            details = rejected.stdout + rejected.stderr
            if rejected.returncode == 0 or not any(
                code in details for code in ("error CS1503", "error CS1929")
            ):
                raise RuntimeError(
                    f"Expected only the removed Server {name} string IP API to fail compilation:\n"
                    + details
                )
            print(f"PASS: Server {name} string IP rejected by C# compiler")
        verify_sealed_builders(folder)
        verify_host_only_logger_fallbacks(folder)
    print("PASS: 3.0 TCP, Builder and logger external-API contracts")


if __name__ == "__main__":
    main()
