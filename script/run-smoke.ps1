param(
    [switch]$NoBuild,
    [string[]]$Demo,
    [string[]]$Test
)

$ErrorActionPreference = "Stop"

$defaultDemos = @(
    "demo/HelloWorld",
    "demo/Streaming",
    "demo/Cancel",
    "demo/Log",
    "demo/Oneway",
    "demo/Timeout",
    "demo/HostApplication"
)

$defaultTests = @(
    "test/SharpLink.IntegrationTests",
    "test/SharpLink.UnitTests"
)

$demos = if ($Demo -and $Demo.Count -gt 0) { $Demo } else { $defaultDemos }
$tests = if ($Test -and $Test.Count -gt 0) { $Test } else { $defaultTests }

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $root

$failed = New-Object System.Collections.Generic.List[string]

function Run-Step {
    param(
        [string]$Name,
        [string]$ProjectPath,
        [switch]$UseNoBuild
    )

    Write-Host ""
    Write-Host "==> $Name :: $ProjectPath"

    $args = @("run", "--project", $ProjectPath)
    if ($UseNoBuild) {
        $args += "--no-build"
    }

    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        $failed.Add("$Name :: $ProjectPath")
    }
}

if (-not $NoBuild) {
    Write-Host "==> build :: Sharplink.slnx"
    dotnet build "Sharplink.slnx" -v minimal
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

foreach ($project in $demos) {
    Run-Step -Name "demo" -ProjectPath $project -UseNoBuild:(-not $NoBuild)
}

foreach ($project in $tests) {
    Run-Step -Name "test" -ProjectPath $project -UseNoBuild:(-not $NoBuild)
}

Write-Host ""
if ($failed.Count -eq 0) {
    Write-Host "Smoke run passed."
    exit 0
}

Write-Host "Smoke run failed:"
foreach ($item in $failed) {
    Write-Host "  - $item"
}
exit 1

