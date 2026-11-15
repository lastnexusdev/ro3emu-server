[CmdletBinding()]
param([switch]$ResetFailures)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot
$taskName = 'UnspokenRo3-Supervisor'
$serverPath = Join-Path $PSScriptRoot 'server\Ro3Emu.Server.exe'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -eq $task) { throw 'The supervisor is not installed. Run Install-Supervisor.ps1 as administrator first.' }
if ($ResetFailures) {
    $crashFile = Join-Path $PSScriptRoot 'logs\server-supervisor-crashes.json'
    if (Test-Path -LiteralPath $crashFile) { Remove-Item -LiteralPath $crashFile }
}
if (-not (Test-Path 'server\game-data-ready.txt')) {
    & .\RO3Emu.exe export C:\Games\Ragnarok3 .\server\data
    if ($LASTEXITCODE -ne 0) { throw 'Game data export failed. Finish the game download before starting the server.' }
    Set-Content 'server\game-data-ready.txt' (Get-Date -Format o)
}
if ($task.State -ne 'Running') { Start-ScheduledTask -TaskName $taskName }
$stateFile = Join-Path $PSScriptRoot 'logs\server-supervisor-state.json'
for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $running = Get-Process -Name 'Ro3Emu.Server' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serverPath }
    try { $state = Get-Content -LiteralPath $stateFile -Raw -ErrorAction Stop | ConvertFrom-Json } catch { $state = $null }
    if ($running -and $state.State -eq 'running' -and $state.ServerPid -eq $running.Id -and (Get-ScheduledTask -TaskName $taskName).State -eq 'Running') {
        Write-Output ('Unspoken Ro is running under the supervisor (PID ' + $running.Id + ').')
        return
    }
    Start-Sleep -Seconds 1
}
throw 'The server did not start within 30 seconds. Check logs\server-supervisor.log and logs\server.stderr.log.'
