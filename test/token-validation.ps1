$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $root 'src\app\bootstrap.ps1')
Initialize-TranscribeEngine
# Import just the validator, without running the downloader or acquiring its mutex.
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'src\app\setup-models.ps1'), [ref]$tokens, [ref]$parseErrors)
$function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Test-Asset' }, $true)
. ([scriptblock]::Create($function.Extent.Text))
$temp = Join-Path ([IO.Path]::GetTempPath()) ('transcribe-tokens-' + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    $writer = [IO.File]::CreateText($temp)
    try {
        for ($id = 0; $id -lt 50256; $id++) { $writer.WriteLine('IQ== ' + $id) }
        $writer.WriteLine('= 50256')
    } finally { $writer.Dispose() }
    if (-not (Test-Asset $temp '')) { throw 'Valid sherpa empty-token marker was rejected.' }
    [WhisperSpeechDecoder]::VerifyTokens($temp)
    # The validation call must not leave a read handle alive.
    $exclusive = [IO.File]::Open($temp, 'Open', 'ReadWrite', 'None'); $exclusive.Dispose()
    [IO.File]::WriteAllText($temp, "= 0`n")
    if (Test-Asset $temp '') { throw 'An empty marker outside the final token was accepted.' }
    $exclusive = [IO.File]::Open($temp, 'Open', 'ReadWrite', 'None'); $exclusive.Dispose()
    $rejected = $false
    try { [WhisperSpeechDecoder]::VerifyTokens($temp) } catch { $rejected = $true }
    if (-not $rejected) { throw 'The engine accepted an invalid token table.' }
    $exclusive = [IO.File]::Open($temp, 'Open', 'ReadWrite', 'None'); $exclusive.Dispose()
    Write-Output 'TOKEN_VALIDATION_OK'
} finally { if (Test-Path -LiteralPath $temp) { Remove-Item -LiteralPath $temp -Force } }
