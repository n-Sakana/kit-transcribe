param([switch]$RequireFresh)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine
$model = Join-Path $root 'src\app\model'
$paths = New-Object System.Collections.Generic.List[string]
foreach ($final in @($false, $true)) {
    $config = [WhisperSpeechDecoder]::CreateConfig($model, $final)
    foreach ($path in @($config.ModelConfig.Whisper.Encoder, $config.ModelConfig.Whisper.Decoder)) {
        if (-not [IO.File]::Exists($path + '.manifest')) { throw ('Bundled manifest is missing: ' + $path) }
        if (-not [IO.File]::Exists($path + '.part01')) { throw ('Bundled binary parts are missing: ' + $path) }
        if ($RequireFresh -and [IO.File]::Exists($path)) { throw ('First-use test requires no assembled model: ' + $path) }
        $paths.Add($path)
    }
}
if ($RequireFresh) { Write-Output 'FRESH_BUNDLE_ONLY_OK' }
# Exercise the normal decoder constructor: it must prepare its own models locally.
& (Join-Path $PSScriptRoot 'model-runtime.ps1')
foreach ($path in $paths) {
    $manifest = [IO.File]::ReadAllLines($path + '.manifest')[0].Split(' ')
    if ((Get-Item -LiteralPath $path).Length -ne [long]$manifest[1]) { throw ('Wrong assembled model length: ' + $path) }
    [WhisperSpeechDecoder]::VerifyFile($path, $manifest[0])
    $timestamp = [IO.File]::GetLastWriteTimeUtc($path)
    [BundledModel]::Ensure($path, $manifest[0])
    if ([IO.File]::GetLastWriteTimeUtc($path) -ne $timestamp) { throw ('Valid model was unnecessarily rewritten: ' + $path) }
}
Write-Output 'OFFLINE_MODELS_OK 4'
