[CmdletBinding()]
param([switch]$Registry, [switch]$Runtime)
Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
foreach ($file in Get-ChildItem -LiteralPath $root -Filter '*.ps1' -Recurse -File) {
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) ('Parse errors in ' + $file.FullName + ': ' + ($errors | Out-String))
}
. (Join-Path $root 'common\menu.ps1')
$plan = @(Get-MenuPlan)
Assert-True ($plan.Count -eq 2) 'Exactly two background contexts are required.'
$expected = @('Software\Classes\Directory\Background\shell\', 'Software\Classes\DesktopBackground\shell\')
for ($i = 0; $i -lt 2; $i++) {
    Assert-True ($plan[$i].Path -eq ($expected[$i] + $script:App.MenuKey)) 'Unexpected context registration.'
    $commands = @($plan[$i].Command) + @($plan[$i].Children | ForEach-Object { $_.Command })
    foreach ($command in $commands) {
        Assert-True ($command -notmatch '%[1Vv]') 'A background-only tool must not consume a selected target.'
        if ($command) { Assert-True ($command.StartsWith('"')) 'Executable must be quoted.' }
    }
}
$entry = Join-Path $root 'src\app\main.ps1'
Assert-True ([IO.File]::Exists($entry)) 'Standalone entry is missing.'
if ($script:App.Id -eq 'keysend') {
    Assert-True ([IO.File]::Exists((Join-Path $root 'common\ui.ps1'))) 'Local UI dependency is missing.'
    Assert-True ($plan[0].Children.Count -eq 2) 'Default and Custom actions must be preserved.'
    Assert-True ($plan[0].Children[0].Command.EndsWith('-Key 0x07 -Idle 120 -MaxRun 0')) 'Default arguments changed.'
    Assert-True ($plan[0].Children[0].Command.Contains('-STA')) 'STA flag is missing.'
}
else {
    Assert-True ($plan[0].Children.Count -eq 0) 'Transcribe must launch directly.'
    Assert-True ([IO.File]::Exists((Join-Path $root 'transcribe.vbs'))) 'Hidden launcher is missing.'
    foreach ($relative in @('engine.cs', 'THIRD-PARTY-NOTICES.md', 'bin\sherpa-onnx.dll', 'bin\NAudio.dll')) {
        Assert-True ([IO.File]::Exists((Join-Path $root ('src\app\' + $relative)))) ('Bundled dependency is missing: ' + $relative)
    }
    $entryRoot = Split-Path (Split-Path (Split-Path $entry -Parent) -Parent) -Parent
    Assert-True ([string]::Equals($entryRoot, $root, [StringComparison]::OrdinalIgnoreCase)) 'Output directory escapes the standalone repository.'
}
Write-Output 'STRUCTURE_OK'
if ($Registry) {
    # Disposable, uniquely named verbs. The real user's app registrations are never touched.
    $script:App.MenuKey += '.Test.' + [Guid]::NewGuid().ToString('N')
    $testPlan = @(Get-MenuPlan)
    try {
        Install-StandaloneMenu
        Install-StandaloneMenu
        foreach ($item in $testPlan) {
            $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($item.Path)
            Assert-True ($null -ne $key) 'Install did not create a context.'
            try {
                Assert-True ($key.GetValue('InstallRoot') -eq $root) 'Wrong install owner.'
                if ($item.Children.Count -gt 0) {
                    foreach ($child in $item.Children) {
                        $commandKey = $key.OpenSubKey('shell\' + $child.Id + '\command')
                        try { Assert-True ($commandKey.GetValue('') -eq $child.Command) 'Wrong action command.' } finally { $commandKey.Dispose() }
                    }
                }
                else {
                    $commandKey = $key.OpenSubKey('command')
                    try { Assert-True ($commandKey.GetValue('') -eq $item.Command) 'Wrong launch command.' } finally { $commandKey.Dispose() }
                }
            }
            finally { $key.Dispose() }
        }
        # Simulate a newer installation: old-copy uninstall must leave its registration intact.
        $foreign = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testPlan[1].Path, $true)
        try { $foreign.SetValue('InstallRoot', 'C:\OtherCopy') } finally { $foreign.Dispose() }
        Uninstall-StandaloneMenu
        $removed = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testPlan[0].Path)
        Assert-True ($null -eq $removed) 'Owned context survived uninstall.'
        $kept = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($testPlan[1].Path)
        Assert-True ($null -ne $kept) 'Another installation was removed.'
        if ($null -ne $kept) { $kept.Dispose() }
        Write-Output 'REGISTRY_OK'
    }
    finally {
        foreach ($item in $testPlan) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($item.Path, $false) }
        Send-MenuChanged
    }
}
if ($Runtime -and $script:App.Id -eq 'transcribe') {
    $ps = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
    & $ps -NoLogo -NoProfile -STA -ExecutionPolicy Bypass -File $entry -Smoke
    if ($LASTEXITCODE -ne 0) { throw 'Transcribe runtime smoke test failed.' }
}
# No KeySend key event or microphone recording is performed by this test.
