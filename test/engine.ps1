$ErrorActionPreference = 'Stop'
# Add-Type assemblies cannot be unloaded. Keep tests independent of earlier runs.
if ('TranscriberEngine' -as [type]) {
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps -NoLogo -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath
    if ($LASTEXITCODE -ne 0) { throw 'Isolated engine regression tests failed.' }
    return
}
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine -AdditionalSource (Join-Path $PSScriptRoot 'engine-tests.cs')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pub-transcribe-tests-' + [Guid]::NewGuid().ToString('N'))
try { [EngineRegressionTests]::Run($temp) }
finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
