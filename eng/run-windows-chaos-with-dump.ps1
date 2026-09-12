param([int]$Attempts = 1)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$output = Join-Path $root 'artifacts/chaos'
New-Item -ItemType Directory -Force $output | Out-Null
$tools = Join-Path $root '.tools/chaos-diagnostics'
dotnet tool install dotnet-dump --version 10.0.745401 --tool-path $tools
if ($LASTEXITCODE -ne 0) { throw 'Could not install the dump collector.' }
dotnet build (Join-Path $root 'test/SharpLink.ChaosTests') -c Release -v minimal
if ($LASTEXITCODE -ne 0) { throw 'Chaos build failed.' }
$dll = Join-Path $root 'test/SharpLink.ChaosTests/bin/Release/net10.0/SharpLink.ChaosTests.dll'
$report = Join-Path $output 'release-smoke.json'
$dump = Join-Path $output 'release-smoke.dmp'
$collector = Join-Path $tools 'dotnet-dump.exe'
for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    if (Test-Path $report) { Remove-Item $report }
    $info = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @($dll, '--duration-seconds', '120', '--transport', 'sharedmemory', '--concurrency', '32', '--restart-interval-seconds', '10', '--json-output', $report)) {
        $info.ArgumentList.Add($argument)
    }
    $process = [System.Diagnostics.Process]::Start($info)
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    $clock = [System.Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    try {
        while (-not $process.WaitForExit(1000)) {
            if (Test-Path $report) {
                try {
                    $checkpoint = Get-Content $report -Raw | ConvertFrom-Json
                    if ($checkpoint.UnexpectedFailures -gt 0) {
                        $failure = "Unexpected chaos failure: $($checkpoint.TerminalFailure.Message)"
                    }
                } catch {
                    # The application atomically rewrites the checkpoint; re-read on the next tick.
                }
            }
            if ($clock.Elapsed.TotalSeconds -gt 180) {
                $failure = 'Chaos exceeded its 120-second workload plus 60-second shutdown bound.'
            }
            if ($failure) {
                Write-Host "$failure Capturing owned test process $($process.Id)."
                $capture = [System.Diagnostics.ProcessStartInfo]::new($collector)
                $capture.UseShellExecute = $false
                foreach ($argument in @('collect', '--process-id', $process.Id.ToString(), '--type', 'Heap', '--output', $dump)) {
                    $capture.ArgumentList.Add($argument)
                }
                $captureProcess = [System.Diagnostics.Process]::Start($capture)
                if (-not $captureProcess.WaitForExit(60000)) {
                    $captureProcess.Kill($true)
                    $captureProcess.WaitForExit()
                    Write-Warning 'Dump capture exceeded 60 seconds.'
                }
                $captureProcess.Dispose()
                if (Test-Path $dump) {
                    Write-Host "Collected dump: $dump ($((Get-Item $dump).Length) bytes)"
                }
                break
            }
        }
        if (-not $failure -and $process.ExitCode -ne 0) {
            $failure = "Chaos exited with code $($process.ExitCode)."
        }
    } finally {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $stdout.GetAwaiter().GetResult() | Set-Content (Join-Path $output "release-smoke-$attempt.stdout.log")
        $stderr.GetAwaiter().GetResult() | Set-Content (Join-Path $output "release-smoke-$attempt.stderr.log")
        Get-Content (Join-Path $output "release-smoke-$attempt.stdout.log")
        Get-Content (Join-Path $output "release-smoke-$attempt.stderr.log")
        $process.Dispose()
    }
    if (Test-Path $report) { Copy-Item $report (Join-Path $output "release-smoke-$attempt.json") }
    if ($failure) { throw $failure }
    Write-Host "Windows chaos attempt $attempt/$Attempts passed."
}
