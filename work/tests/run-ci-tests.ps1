param(
    [string]$OutputDirectory = '',
    [ValidateRange(60, 1800)][int]$StepTimeoutSeconds = 300
)

# Fixed, offline CI entry points. Do not enumerate every *tests.ps1 file: some
# scripts use real accounts, public networking, Inno Setup, or installed apps.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$ciRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'outputs\ci'))
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $ciRoot 'windows' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (-not $OutputDirectory.StartsWith($ciRoot.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'CI output must be a child directory of this repository outputs\ci.'
}

# Refuse reparse points in an existing output path before writing any logs.
$current = $OutputDirectory
while ($current -and $current.Length -ge $repositoryRoot.Length) {
    if (Test-Path -LiteralPath $current) {
        if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'CI output cannot traverse a reparse point.'
        }
    }
    if ($current -eq $repositoryRoot) { break }
    $current = [IO.Path]::GetDirectoryName($current)
}
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false)
$summaryPath = Join-Path $OutputDirectory 'summary.json'
$steps = New-Object 'Collections.Generic.List[object]'
$started = [DateTime]::UtcNow
$summary = [ordered]@{
    schemaVersion = 1
    status = 'running'
    startedAtUtc = $started.ToString('o')
    finishedAtUtc = $null
    buildPurpose = 'development-only; not a release or public installer'
    testScope = 'offline, isolated regression tests; no real login, billing, public-update smoke, or Inno Setup'
    environment = [ordered]@{}
    steps = @()
    error = $null
}

function Write-CiSummary {
    $summary.steps = @($steps.ToArray())
    [IO.File]::WriteAllText($summaryPath, ($summary | ConvertTo-Json -Depth 8), $utf8)
}

function ConvertTo-CiProcessArgument([string]$Value) {
    # All current arguments are trusted paths or constants. Explicit quoting is
    # still needed because Start-Process joins ArgumentList with spaces.
    if ($Value.IndexOf('"') -ge 0 -or $Value.IndexOf([char]0) -ge 0) {
        throw 'Unexpected quote or null in a CI process argument.'
    }
    return '"' + $Value.TrimEnd('\') + '"'
}

function Write-CiLog([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    # Native .NET tests may emit the Windows code page under PowerShell 5.1.
    # Normalize saved logs to UTF-8 without garbling localized failure text.
    try { $content = [IO.File]::ReadAllText($Path, [Text.UTF8Encoding]::new($false, $true)) }
    catch [Text.DecoderFallbackException] { $content = [IO.File]::ReadAllText($Path, [Text.Encoding]::Default) }
    [IO.File]::WriteAllText($Path, $content, $utf8)
    if ($content) { Write-Output $content.TrimEnd([char[]]@("`r", "`n")) }
}

function Invoke-CiStep([string]$Name, [string]$Script, [string[]]$ScriptArguments = @()) {
    if ($Name -cnotmatch '^[a-z0-9-]+$') { throw 'Invalid CI step name.' }
    if (-not (Test-Path -LiteralPath $Script -PathType Leaf)) { throw "Missing CI script: $Script" }
    $stdoutPath = Join-Path $OutputDirectory ($Name + '.stdout.log')
    $stderrPath = Join-Path $OutputDirectory ($Name + '.stderr.log')
    $step = [ordered]@{
        name = $Name
        script = $Script.Substring($repositoryRoot.Length + 1).Replace('\', '/')
        status = 'running'
        startedAtUtc = [DateTime]::UtcNow.ToString('o')
        durationSeconds = $null
        exitCode = $null
        stdout = $Name + '.stdout.log'
        stderr = $Name + '.stderr.log'
    }
    $steps.Add($step)
    Write-CiSummary
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Write-Output ("[CI] Running {0}" -f $Name)
    $arguments = @('-NoLogo', '-NoProfile', '-NonInteractive', '-STA', '-ExecutionPolicy', 'RemoteSigned', '-File', (ConvertTo-CiProcessArgument $Script))
    foreach ($argument in $ScriptArguments) { $arguments += ConvertTo-CiProcessArgument $argument }
    $process = $null
    try {
        $process = Start-Process -FilePath $script:ciPowerShell -ArgumentList $arguments -WorkingDirectory $repositoryRoot -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
        # PowerShell 5.1 can return a null ExitCode after WaitForExit if the
        # process handle was not retained while the child was still running.
        $null = $process.Handle
        if (-not $process.WaitForExit($StepTimeoutSeconds * 1000)) {
            # Kill only this still-running CI process and its test children.
            # Never search for or terminate a user's installed widget by name.
            if (-not $process.HasExited) {
                $taskKill = Join-Path $env:WINDIR 'System32\taskkill.exe'
                & $taskKill /PID $process.Id /T /F 2>&1 | Out-Null
            }
            $step.status = 'timed-out'
            throw ("CI step {0} exceeded {1} seconds." -f $Name, $StepTimeoutSeconds)
        }
        $process.WaitForExit()
        $step.exitCode = $process.ExitCode
        $step.status = if ($process.ExitCode -eq 0) { 'passed' } else { 'failed' }
        if ($process.ExitCode -ne 0) { throw ("CI step {0} failed with exit code {1}." -f $Name, $process.ExitCode) }
    }
    catch {
        if ($step.status -eq 'running') { $step.status = 'failed' }
        throw
    }
    finally {
        $watch.Stop()
        $step.durationSeconds = [Math]::Round($watch.Elapsed.TotalSeconds, 3)
        foreach ($log in @($stdoutPath, $stderrPath)) { Write-CiLog $log }
        if ($null -ne $process) { $process.Dispose() }
        Write-CiSummary
    }
    Write-Output ("[CI] Passed {0} ({1}s)" -f $Name, $step.durationSeconds)
}

# Every entry is intentional. Suites that compile to the same test folder must
# remain sequential. Additional release and live-device checks are separate.
$testNames = @(
    'license-notices-tests',
    'app-icon-tests',
    'backend-tests',
    'free-plan-tests',
    'storage-tests',
    'profile-migration-tests',
    'widget-startup-tests',
    'extra-storage-tests',
    'refresh-schedule-tests',
    'membership-config-tests',
    'membership-client-tests',
    'membership-ui-tests',
    'widget-ui-tests',
    'widget-isolation-tests',
    'widget-presentation-tests',
    'localization-tests',
    'startup-refresh-tests',
    'usage-state-consistency-tests',
    'connection-tests',
    'connection-flow-tests',
    'service-operation-tests',
    'onboarding-tests',
    'codex-installation-discovery-tests',
    'codex-login-tests',
    'codex-auto-connect-tests',
    'claude-tests',
    'claude-receipt-ui-tests',
    'claude-environment-ui-tests',
    'wsl-runtime-tests',
    'wsl-ui-tests',
    'higgsfield-tests',
    'higgsfield-ui-tests',
    'compact-view-tests',
    'compact-auto-geometry-tests',
    'compact-auto-integration-tests',
    'dashboard-ui-tests',
    'dashboard-state-tests',
    'dashboard-timeline-tests',
    'dashboard-history-insights-tests',
    'dashboard-history-refresh-tests',
    'dashboard-preferences-tests',
    'weekly-summary-tests',
    'weekly-summary-ui-tests',
    'usage-tracking-tests',
    'usage-analytics-tests',
    'usage-analytics-ui-tests',
    'usage-analysis-integration-tests',
    'observed-history-tests',
    'auto-update-tests',
    'auto-update-ui-tests',
    'powershell-install-tests'
)

try {
    Write-CiSummary
    if ($env:OS -ne 'Windows_NT') { throw 'Windows CI requires Windows.' }
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -ne 5 -or $PSVersionTable.PSVersion.Minor -ne 1) {
        throw 'Windows CI requires Windows PowerShell 5.1 (shell: powershell), not PowerShell 7.'
    }
    if (-not [Environment]::Is64BitProcess) { throw 'Windows CI requires 64-bit Windows PowerShell.' }
    if ([Threading.Thread]::CurrentThread.ApartmentState -ne 'STA') { throw 'Windows CI requires STA for WPF regression tests.' }
    $frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
    foreach ($relative in @('csc.exe', 'System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Net.Http.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll', 'WPF\PresentationFramework.dll', 'WPF\PresentationCore.dll', 'WPF\WindowsBase.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $frameworkDirectory $relative) -PathType Leaf)) {
            throw ("Required .NET Framework / WPF component is missing: {0}" -f $relative)
        }
    }
    $frameworkRelease = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -Name Release).Release
    if ($frameworkRelease -lt 528040) { throw '.NET Framework 4.8 or later is required.' }
    $script:ciPowerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $script:ciPowerShell -PathType Leaf)) { throw 'Windows PowerShell executable is missing.' }
    Add-Type -AssemblyName PresentationFramework
    $null = New-Object Windows.Controls.Grid
    $summary.environment = [ordered]@{
        powershell = $PSVersionTable.PSVersion.ToString()
        edition = $PSVersionTable.PSEdition
        bitness = 64
        apartmentState = [Threading.Thread]::CurrentThread.ApartmentState.ToString()
        frameworkRelease = $frameworkRelease
        suiteCount = $testNames.Count
        perStepTimeoutSeconds = $StepTimeoutSeconds
    }
    Write-CiSummary
    $buildDirectory = Join-Path $OutputDirectory 'development-build'
    $exampleConfig = Join-Path $repositoryRoot 'work\membership\client-config.example.json'
    Invoke-CiStep 'development-build' (Join-Path $repositoryRoot 'work\build.ps1') @('-OutputDirectory', $buildDirectory, '-MembershipConfig', $exampleConfig)
    foreach ($name in $testNames) {
        if ($name -eq 'app-icon-tests') {
            Invoke-CiStep $name (Join-Path $PSScriptRoot ($name + '.ps1')) @('-IconPath', (Join-Path $buildDirectory 'widget.ico'))
        } elseif ($name -eq 'widget-isolation-tests') {
            Invoke-CiStep $name (Join-Path $PSScriptRoot ($name + '.ps1')) @('-Executable', (Join-Path $buildDirectory 'UsageWidget.exe'))
        } else {
            Invoke-CiStep $name (Join-Path $PSScriptRoot ($name + '.ps1'))
        }
    }
    $summary.status = 'passed'
}
catch {
    $summary.status = 'failed'
    $summary.error = $_.Exception.Message
    Write-Error -Message $_.Exception.Message -ErrorAction Continue
}
finally {
    $summary.finishedAtUtc = [DateTime]::UtcNow.ToString('o')
    Write-CiSummary
}
if ($summary.status -ne 'passed') { exit 1 }
Write-Output ("[CI] All {0} offline suites and the development build passed. Summary: {1}" -f $testNames.Count, $summaryPath)
exit 0
