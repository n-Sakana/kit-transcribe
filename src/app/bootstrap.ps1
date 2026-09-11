function Initialize-TranscribeEngine {
    param([string]$AdditionalSource = "")
    if (-not [Environment]::Is64BitProcess) {
        throw '64-bit Windows PowerShell is required. Launch transcribe.cmd, not a 32-bit PowerShell host.'
    }
    if ($PSVersionTable.PSEdition -ne 'Desktop' -or $PSVersionTable.PSVersion.Major -lt 5) {
        throw 'Use Windows PowerShell 5.1 (powershell.exe), not PowerShell 7 (pwsh.exe).'
    }
    if ('TranscriberEngine' -as [type]) { return }
    $bin = Join-Path $PSScriptRoot 'bin'
    $hashes = @{
        'NAudio.dll' = 'BC4BACC3B8B28D898F1671B79F216CCA439F95EB60CD32D3E3ECAFBECAC42780'
        'sherpa-onnx.dll' = 'B6D9A12D659C742A3D9E4D72204186B50AFBD6C56A0F36893F2DC0DCE627245F'
        'sherpa-onnx-c-api.dll' = '614878147C05121AEB1514EC4FB3E48B89751591532ECA9208235B9AB868306A'
        'onnxruntime.dll' = 'DAA77083A45BF525DA0DDE9E87F85D8EB146F58F9C9AA7124CA84545E1C0F148'
    }
    foreach ($name in $hashes.Keys) {
        $path = Join-Path $bin $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Bundled DLL missing: $path" }
        if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hashes[$name]) {
            throw "Bundled DLL checksum mismatch; restore the original file: $path"
        }
    }
    $env:PATH = $bin + ';' + $env:PATH
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class TranscribeDllSearch {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool SetDllDirectory(string path);
}
'@
    if (-not [TranscribeDllSearch]::SetDllDirectory($bin)) { throw 'Could not configure the native DLL directory.' }
    Add-Type -Path (Join-Path $bin 'sherpa-onnx.dll')
    Add-Type -Path (Join-Path $bin 'NAudio.dll')
    $references = @('System.dll', 'System.Core.dll', 'System.Windows.Forms.dll',
        (Join-Path $bin 'sherpa-onnx.dll'), (Join-Path $bin 'NAudio.dll'))
    $source = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'engine.cs'))
    if ($AdditionalSource) {
        $extra = [IO.File]::ReadAllText($AdditionalSource) -replace '(?m)^using [^;]+;\r?\n', ''
        $source = 'using System.Diagnostics;' + "`r`n" + $source + "`r`n" + $extra
    }
    Add-Type -TypeDefinition $source -ReferencedAssemblies $references
}
