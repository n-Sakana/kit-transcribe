[CmdletBinding()]
param([switch]$PlanOnly)
$ErrorActionPreference = 'Stop'
try {
    . (Join-Path $PSScriptRoot 'menu.ps1')
    if ($PlanOnly) { @(Get-MenuPlan) | ConvertTo-Json -Depth 6; exit 0 }
    Install-StandaloneMenu
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
