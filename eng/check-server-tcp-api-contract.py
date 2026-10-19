#!/usr/bin/env python3
"""External consumer compilation gate for 3.0's Server-only typed IPAddress TCP API.

This compiles a fresh project *outside* the repository against the real production
projects. The negative compilations must fail for the removed Server overloads;
the positive compilation must continue to accept Server IPAddress, Client string
IP and standalone RuntimeContext. No runtime socket or benchmark workload runs.
"""

from pathlib import Path
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parents[1]

CLIENT = ROOT / "src/SharpLink.Client/SharpLink.Client.csproj"
SERVER = ROOT / "src/SharpLink.Server/SharpLink.Server.csproj"
RUNTIME = ROOT / "src/SharpLink.Runtime/SharpLink.Runtime.csproj"
SERVER_SOURCE = ROOT / "src/SharpLink.Server/TransportExtensions.cs"
CLIENT_SOURCE = ROOT / "src/SharpLink.Client/TransportExtensions.cs"
SERVER_BASELINE = ROOT / "eng/public-api/3.0.0/SharpLink.Server.api.txt"

PREFIX = """
using System.Net;
using System.Net.Security;
using SharpLink.Client;
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
        using var runtime = new SharpLinkRuntimeContextBuilder().Build();
        _ = runtime.Codecs.GetCodec<int>();
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


def compile_project(code: str, path: Path) -> subprocess.CompletedProcess[str]:
    (path / "Probe.cs").write_text(PREFIX + code + SUFFIX, encoding="utf-8")
    return run("dotnet", "build", str(path / "Consumer.csproj"),
               "-c", "Release", "--no-restore", "--verbosity", "minimal", cwd=path)


def main() -> None:
    verify_static_contract()
    with tempfile.TemporaryDirectory(prefix="sharplink-external-tcp-") as location:
        folder = Path(location)
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
    print("PASS: 3.0 Server TCP external-API contract")


if __name__ == "__main__":
    main()
