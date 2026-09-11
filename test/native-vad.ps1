$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine
$model = Join-Path $root 'src\app\model'
foreach ($final in @($false, $true)) {
    $config = [WhisperSpeechDecoder]::CreateVadConfig($model, $final)
    $vad = New-Object SherpaOnnx.VoiceActivityDetector -ArgumentList $config, ([single]40)
    try {
        $field = $vad.GetType().GetField('_handle', [Reflection.BindingFlags]'Instance,NonPublic')
        if ($field.GetValue($vad).Handle -eq [IntPtr]::Zero) { throw 'Native VAD initialization returned null.' }
        $vad.AcceptWaveform((New-Object 'single[]' 16000))
        $vad.Flush()
        if (-not $vad.IsEmpty()) { throw 'Silence should not produce speech segments.' }
    } finally { $vad.Dispose() }
}
Write-Output 'NATIVE_VAD_OK'
