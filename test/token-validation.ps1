$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine
$temp = Join-Path ([IO.Path]::GetTempPath()) ('transcribe-tokens-' + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    $writer = [IO.File]::CreateText($temp)
    try {
        for ($id = 0; $id -lt 50256; $id++) { $writer.WriteLine('IQ== ' + $id) }
        $writer.WriteLine('= 50256')
    } finally { $writer.Dispose() }
    [WhisperSpeechDecoder]::VerifyTokens($temp)
    $exclusive = [IO.File]::Open($temp, 'Open', 'ReadWrite', 'None'); $exclusive.Dispose()
    [IO.File]::WriteAllText($temp, "= 0`n")
    $rejected = $false
    try { [WhisperSpeechDecoder]::VerifyTokens($temp) } catch { $rejected = $true }
    if (-not $rejected) { throw 'The engine accepted an invalid token table.' }
    $exclusive = [IO.File]::Open($temp, 'Open', 'ReadWrite', 'None'); $exclusive.Dispose()
    Write-Output 'TOKEN_VALIDATION_OK'
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
