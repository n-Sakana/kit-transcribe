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
            # Bypass VAD only in this smoke test: silence otherwise never exercises Whisper.
            $field = $decoder.GetType().GetField('recognizer', [Reflection.BindingFlags]'Instance,NonPublic')
            $recognizer = $field.GetValue($decoder)
            $stream = $recognizer.CreateStream()
            try {
                if ($stream.Handle -eq [IntPtr]::Zero) { throw 'Whisper stream initialization returned null.' }
                $stream.AcceptWaveform(16000, (New-Object 'single[]' 16000))
                $recognizer.Decode($stream)
                [void]$stream.Result.Text
                Write-Output ('NATIVE_DECODE_OK ' + $name)
            } finally { $stream.Dispose() }
        } finally { $decoder.Dispose() }
    }
    Write-Output ('MODEL_RUNTIME_OK ' + $name)
}
