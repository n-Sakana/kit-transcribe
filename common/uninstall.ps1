[CmdletBinding()]
param([switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'menu.ps1')
    if ($PlanOnly) { @(Get-MenuPlan) | ForEach-Object { 'HKEY_CURRENT_USER\' + $_.Path }; exit 0 }
    Uninstall-StandaloneMenu
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
