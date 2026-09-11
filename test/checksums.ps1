$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$count = 0
$mismatches = New-Object System.Collections.Generic.List[string]
foreach ($line in [IO.File]::ReadAllLines((Join-Path $root 'SHA256SUMS.txt'))) {
    if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw ('Invalid checksum line: ' + $line) }
    $expected = $matches[1]; $path = $matches[2]
    if ($path.Contains('"')) { throw 'Invalid checksum path.' }
    # Hash the Git blob bytes, not CRLF-converted Windows working-tree bytes.
    $process = New-Object Diagnostics.Process
    $process.StartInfo = New-Object Diagnostics.ProcessStartInfo
    $process.StartInfo.FileName = 'git'
    $process.StartInfo.Arguments = 'cat-file blob "HEAD:' + $path + '"'
    $process.StartInfo.WorkingDirectory = $root
    $process.StartInfo.UseShellExecute = $false
    $process.StartInfo.RedirectStandardOutput = $true
    $process.StartInfo.RedirectStandardError = $true
    $process.StartInfo.CreateNoWindow = $true
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        [void]$process.Start()
        $actual = [BitConverter]::ToString($sha.ComputeHash($process.StandardOutput.BaseStream)).Replace('-', '').ToLowerInvariant()
        $errorText = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw ('git cat-file failed: ' + $errorText) }
        if ($actual -ne $expected) {
            $mismatches.Add($path)
            Write-Output ('CHECKSUM_MISMATCH ' + $actual + '  ' + $path)
        }
        $count++
    } finally { $sha.Dispose(); $process.Dispose() }
}
if ($mismatches.Count -gt 0) { throw ('Checksum mismatches: ' + ($mismatches -join ', ')) }
Write-Output ('CHECKSUMS_OK ' + $count)
