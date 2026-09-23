# Run with Windows PowerShell 5.1 and a .NET SDK. No application startup or real VPN calls.
[CmdletBinding()]
param([switch]$SkipScheduler)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$probeId = 'AutoVPNConnect-Probe-' + [Guid]::NewGuid().ToString('N')
$scratch = Join-Path ([IO.Path]::GetTempPath()) $probeId
try {
    New-Item -ItemType Directory -Path $scratch | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'RecoveryChecks.csproj'), (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'SettingsManager.cs'), (Join-Path $PSScriptRoot 'ConnectionLog.cs') -Destination $scratch
    Copy-Item -LiteralPath (Join-Path $repo 'AutoVPNConnect/ConnectionManager.cs') -Destination $scratch
    $source = [IO.File]::ReadAllText((Join-Path $repo 'AutoVPNConnect/RasManService.cs'))
    # Change only the scheduler namespace and wait budget in a disposable copy. The real
    # task name can never be queried or triggered by the probe, even if it already exists.
    $folder = 'private const string TaskFolder = "AutoVPNConnect";'
    $timeout = 'private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(2);'
    if (-not $source.Contains($folder) -or -not $source.Contains($timeout)) {
        throw 'Probe substitutions no longer match RasManService.cs; review the harness.'
    }
    $source = $source.Replace($folder, ('private const string TaskFolder = "' + $probeId + '";')).Replace($timeout, 'private static readonly TimeSpan TaskTimeout = TimeSpan.FromSeconds(3);')
    [IO.File]::WriteAllText((Join-Path $scratch 'RasManService.cs'), $source)
    & dotnet build (Join-Path $scratch 'RecoveryChecks.csproj') -c Release --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Probe build failed.' }
    $probeArgs = @()
    if ($SkipScheduler) { $probeArgs += '--skip-scheduler' }
    & (Join-Path $scratch 'bin/Release/net472/RecoveryChecks.exe') @probeArgs
    if ($LASTEXITCODE -ne 0) { throw 'Recovery checks failed.' }
}
finally {
    # Delete only the unique directory created by this invocation.
    $expected = [IO.Path]::GetFullPath((Join-Path ([IO.Path]::GetTempPath()) $probeId))
    if ([IO.Path]::GetFullPath($scratch) -ne $expected -or $probeId -notmatch '^AutoVPNConnect-Probe-[0-9a-f]{32}$') {
        throw 'Unexpected probe cleanup path.'
    }
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
