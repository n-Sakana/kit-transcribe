# Firewall changes are restricted to an ephemeral GitHub Actions test machine.
$ErrorActionPreference = 'Stop'
if ($env:GITHUB_ACTIONS -ne 'true') { throw 'CI only. Run test/offline-models.ps1 for a local test without firewall changes.' }
$program = (Get-Process -Id $PID).Path
$ruleName = 'pub-transcribe-offline-' + [Guid]::NewGuid().ToString('N')
$profiles = @(Get-NetFirewallProfile | Select-Object Name, Enabled)
try {
    foreach ($profile in $profiles) { Set-NetFirewallProfile -Name $profile.Name -Enabled True }
    [void](New-NetFirewallRule -Name $ruleName -DisplayName $ruleName -Direction Outbound -Program $program -Action Block -Profile Any -Enabled True)
    $rule = Get-NetFirewallRule -Name $ruleName
    if ($rule.Enabled -ne 'True' -or $rule.Action -ne 'Block') { throw 'Could not enforce offline application test.' }
    Write-Output ('OFFLINE_FIREWALL_ENABLED ' + $program)
    & (Join-Path $PSScriptRoot 'offline-models.ps1') -RequireFresh
} finally {
    Remove-NetFirewallRule -Name $ruleName -ErrorAction SilentlyContinue
    foreach ($profile in $profiles) { Set-NetFirewallProfile -Name $profile.Name -Enabled $profile.Enabled }
}
