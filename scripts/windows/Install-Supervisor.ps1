[CmdletBinding()]
param([switch]$NoStart)
$ErrorActionPreference = 'Stop'
$taskName = 'UnspokenRo3-Supervisor'
$watcher = Join-Path $PSScriptRoot 'Watch-Server.ps1'
if (-not (Test-Path -LiteralPath $watcher)) { throw 'Watch-Server.ps1 is missing.' }
$powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
$action = New-ScheduledTaskAction -Execute $powershell -Argument ('-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $watcher + '"') -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtStartup
$trigger.Delay = 'PT20S'
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([timespan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Runs RO3Emu at Windows startup. Restarts after panel restart or crashes; respects intentional shutdown.' -Force | Out-Null
if (-not $NoStart) { Start-ScheduledTask -TaskName $taskName }
Write-Output ('Installed ' + $taskName + '. Existing servers are adopted without disconnecting players.')
