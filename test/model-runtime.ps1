param([string]$Wav = '')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine
$model = Join-Path $root 'src\app\model'
foreach ($final in @($false, $true)) {
    $name = if ($final) { 'large-v3-turbo' } else { 'small' }
    if ($Wav) {
        [TranscriberEngine]::DecodeFileSegments($model, (Resolve-Path -LiteralPath $Wav).Path, $final)
    } else {
        $decoder = New-Object WhisperSpeechDecoder -ArgumentList $model, $final
        try {
            [void]$decoder.Feed((New-Object 'single[]' 16000))
            [void]$decoder.Flush()
        } finally { $decoder.Dispose() }
    }
    Write-Output ('MODEL_RUNTIME_OK ' + $name)
}
