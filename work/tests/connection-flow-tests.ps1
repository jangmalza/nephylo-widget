$ErrorActionPreference = 'Stop'
$testDirectory = $PSScriptRoot
$sourceDirectory = Join-Path (Split-Path -Parent $testDirectory) 'src'
$testExecutable = Join-Path $testDirectory 'connection-flow-tests.exe'
$frameworkDirectory = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$compiler = Join-Path $frameworkDirectory 'csc.exe'
$arguments = @('/nologo', '/warn:4', '/langversion:5', '/target:exe', '/main:ConnectionFlowTests', '/utf8output', ('/out:' + $testExecutable))
foreach ($reference in @('System.dll', 'System.Core.dll', 'System.Web.Extensions.dll', 'System.Security.dll', 'System.Net.Http.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Xaml.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory $reference)
}
foreach ($reference in @('PresentationFramework.dll', 'PresentationCore.dll', 'WindowsBase.dll')) {
    $arguments += '/reference:' + (Join-Path $frameworkDirectory ('WPF\' + $reference))
}
$arguments += @(Get-ChildItem -LiteralPath $sourceDirectory -Filter '*.cs' | ForEach-Object { $_.FullName })
$arguments += Join-Path $testDirectory 'connection-flow-tests.cs'
$arguments += Join-Path $testDirectory 'claude-quota-fixture.cs'
$arguments += Join-Path $testDirectory 'support-diagnostics-tests.cs'
& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'Connection flow tests did not compile.' }
& $testExecutable
if ($LASTEXITCODE -ne 0) { throw 'Connection flow tests failed.' }
