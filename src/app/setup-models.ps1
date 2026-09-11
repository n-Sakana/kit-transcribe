[CmdletBinding()]
param([ValidateSet('all', 'small', 'turbo')][string]$Models = 'all')
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$modelRoot = Join-Path $PSScriptRoot 'model'
$sets = @(
    @{ Name = 'small'; Directory = 'whisper-small'; Revision = '8f3c18b358db4d1f2fc1eae49d75cd20989e4309';
       EncoderHash = '4cbe7b22fa9026b843b60a68640c747de05bafb1a11b57edc0e66c232d9f33a9';
       DecoderHash = 'acad50b5c782696e91b55914cc5ab4f756f1532f76e22aa6fc615f39fb69a8ee' },
    @{ Name = 'turbo'; Directory = 'whisper-large-v3-turbo'; Revision = '2ca6ff69fc878651b770880507669577ac41c2ff';
       EncoderHash = 'b02dcdf54f348741e93fe732b67d933c8dcb6735655f710640143081db38878b';
       DecoderHash = '20accd02388482eb3a46bd615631adfdc85e1eb2c7db9ea3f02a40ffe6b81547' }
)
function Test-Asset([string]$Path, [string]$Hash) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    if ($Hash) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Hash }
    # Tokens are fetched from the same pinned revision, never from a moving 'main'.
    $id = 0
    # Explicit disposal also covers early validation failures in Windows PowerShell.
    $reader = [IO.File]::OpenText($Path)
    try {
        while ($null -ne ($line = $reader.ReadLine())) {
            $split = $line.LastIndexOf(' ')
            $number = 0
            if ($split -le 0 -or -not [int]::TryParse($line.Substring($split + 1), [ref]$number) -or $number -ne $id) { return $false }
            $token = $line.Substring(0, $split)
            if ($id -eq 50256) {
                if ($token -ne '=') { return $false }
            } else {
                try { [void][Convert]::FromBase64String($token) } catch { return $false }
            }
            $id++
        }
    } finally { $reader.Dispose() }
    return $id -eq 50257
}
$mutex = New-Object Threading.Mutex -ArgumentList $false, 'Local\pub-transcribe-model-setup'
$owned = $false
try {
    try { $owned = $mutex.WaitOne([TimeSpan]::FromMinutes(30)) }
    catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { throw 'Another model setup is still running.' }
    foreach ($set in $sets) {
        if ($Models -ne 'all' -and $Models -ne $set.Name) { continue }
        $directory = Join-Path $modelRoot $set.Directory
        [void][IO.Directory]::CreateDirectory($directory)
        $baseUrl = 'https://huggingface.co/csukuangfj/sherpa-onnx-whisper-' + $set.Name + '/resolve/' + $set.Revision + '/'
        $files = @(
            @{ Name = $set.Name + '-encoder.int8.onnx'; Hash = $set.EncoderHash },
            @{ Name = $set.Name + '-decoder.int8.onnx'; Hash = $set.DecoderHash },
            @{ Name = $set.Name + '-tokens.txt'; Hash = '' }
        )
        foreach ($file in $files) {
            $path = Join-Path $directory $file.Name
            if (Test-Asset $path $file.Hash) { Write-Host ('Verified: ' + $file.Name); continue }
            $temp = $path + '.download.' + [Guid]::NewGuid().ToString('N')
            try {
                for ($attempt = 1; $attempt -le 3; $attempt++) {
                    $client = New-Object Net.WebClient
                    try {
                        Write-Host ('Downloading ' + $file.Name + ' (attempt ' + $attempt + '/3)...')
                        $client.DownloadFile(($baseUrl + $file.Name), $temp)
                        if (-not (Test-Asset $temp $file.Hash)) { throw ('Downloaded asset validation failed: ' + $file.Name) }
                        break
                    } catch {
                        if ($attempt -eq 3) { throw }
                        Write-Warning $_.Exception.Message
                        Start-Sleep -Seconds 2
                    } finally { $client.Dispose() }
                }
                # Never expose partial downloads as valid models.
                if (Test-Path -LiteralPath $path) { [IO.File]::Replace($temp, $path, $null) }
                else { [IO.File]::Move($temp, $path) }
                Write-Host ('Installed: ' + $path)
            } finally {
                if (Test-Path -LiteralPath $temp) {
                    try { Remove-Item -LiteralPath $temp -Force }
                    catch { Write-Warning ('Could not remove temporary download: ' + $_.Exception.Message) }
                }
            }
        }
    }
    Write-Host 'MODEL_SETUP_OK - Recognition runs locally; no audio is sent to the download provider.'
} catch {
    [Console]::Error.WriteLine($_.Exception.ToString())
    exit 1
} finally {
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
