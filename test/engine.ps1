$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine -AdditionalSource (Join-Path $PSScriptRoot 'engine-tests.cs')
$temp = Join-Path ([IO.Path]::GetTempPath()) ('pub-transcribe-tests-' + [Guid]::NewGuid().ToString('N'))
try { [EngineRegressionTests]::Run($temp) }
finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Recurse -Force } }
