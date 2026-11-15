[CmdletBinding()]
param([string]$ConfigurationFile)

$ErrorActionPreference = 'Stop'
$serverExecutable = Join-Path $PSScriptRoot 'server\Ro3Emu.Server.exe'
$serverDirectory = Join-Path $PSScriptRoot 'server'
$logDirectory = Join-Path $PSScriptRoot 'logs'
$serverArguments = @()
$skipAdoption = $false
if ($ConfigurationFile) {
    $configuration = Get-Content -LiteralPath $ConfigurationFile -Raw | ConvertFrom-Json
    $serverExecutable = [IO.Path]::GetFullPath($configuration.Executable)
    $serverDirectory = [IO.Path]::GetFullPath($configuration.WorkingDirectory)
    $logDirectory = [IO.Path]::GetFullPath($configuration.LogDirectory)
    $serverArguments = @($configuration.Arguments)
    $skipAdoption = [bool]$configuration.SkipAdoption
}
if (-not (Test-Path -LiteralPath $serverExecutable -PathType Leaf)) { throw "Server executable not found: $serverExecutable" }
New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$stateFile = Join-Path $logDirectory 'server-supervisor-state.json'
$supervisorLog = Join-Path $logDirectory 'server-supervisor.log'
$crashFile = Join-Path $logDirectory 'server-supervisor-crashes.json'
$hashAlgorithm = [Security.Cryptography.SHA256]::Create()
try { $instanceHash = [BitConverter]::ToString($hashAlgorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($logDirectory.ToLowerInvariant()))).Replace('-', '').Substring(0, 16) }
finally { $hashAlgorithm.Dispose() }
$mutex = New-Object Threading.Mutex($false, ('Global\Ro3EmuSupervisor-' + $instanceHash))
$owned = $false
$supervisorExit = 0
$child = $null
$started = [datetime]::UtcNow.ToString('o')

function Write-SupervisorLog([string]$Message) {
    Add-Content -LiteralPath $supervisorLog -Value (([datetime]::UtcNow.ToString('o')) + ' ' + $Message) -Encoding UTF8
}

function Write-SupervisorState([string]$State, [object]$ChildId, [object]$ExitCode, [int]$Failures = 0) {
    $payload = [ordered]@{
        State = $State; SupervisorPid = $PID; ServerPid = $ChildId; LastExitCode = $ExitCode
        StartedUtc = $started; UpdatedUtc = [datetime]::UtcNow.ToString('o'); RecentCrashes = $Failures
        Identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
    }
    $temporary = $stateFile + '.tmp'
    [IO.File]::WriteAllText($temporary, ($payload | ConvertTo-Json), (New-Object Text.UTF8Encoding($false)))
    Move-Item -LiteralPath $temporary -Destination $stateFile -Force
}

function Get-RecentCrashes {
    $cutoff = [datetime]::UtcNow.AddMinutes(-10)
    if (Test-Path -LiteralPath $crashFile) {
        foreach ($timestamp in @(Get-Content -LiteralPath $crashFile -Raw | ConvertFrom-Json)) {
            $parsed = [datetime]::Parse($timestamp, [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind)
            if ($parsed -gt $cutoff) { $parsed.ToUniversalTime() }
        }
    }
}

function Archive-ServerLogs {
    $archive = Join-Path $logDirectory 'server-history'
    New-Item -ItemType Directory -Path $archive -Force | Out-Null
    $stamp = [datetime]::UtcNow.ToString('yyyyMMdd-HHmmss-fffffff')
    foreach ($kind in @('stdout', 'stderr')) {
        $file = Join-Path $logDirectory ('server.' + $kind + '.log')
        if (Test-Path -LiteralPath $file) {
            Move-Item -LiteralPath $file -Destination (Join-Path $archive ('server-' + $stamp + '.' + $kind + '.log'))
        }
    }
    $archiveRoot = [IO.Path]::GetFullPath($archive).TrimEnd('\') + '\'
    Get-ChildItem -LiteralPath $archive -Filter 'server-*.log' -File | Sort-Object Name -Descending | Select-Object -Skip 40 | ForEach-Object {
        if (-not $_.FullName.StartsWith($archiveRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Log archive path escaped its directory.' }
        Remove-Item -LiteralPath $_.FullName
    }
}

function Find-ExistingServer {
    if ($skipAdoption) { return }
    $name = [IO.Path]::GetFileNameWithoutExtension($serverExecutable)
    $candidates = @(Get-Process -Name $name -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $serverExecutable })
    if ($candidates.Count -gt 1) { throw 'More than one matching server process exists; refusing to launch another.' }
    if ($candidates.Count -eq 1) { return $candidates[0] }
}

try {
    try { $owned = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $owned = $true }
    if (-not $owned) { exit 0 }
    Write-SupervisorLog 'Supervisor started. Exit 75 restarts; exit 0 stays stopped; unexpected exits recover with a delay.'
    while ($true) {
        $crashes = @(Get-RecentCrashes)
        if ($crashes.Count -ge 5) {
            Write-SupervisorLog 'Five crashes within ten minutes. Automatic launches paused; fix the cause and run Start-Server.ps1 -ResetFailures.'
            Write-SupervisorState 'faulted' $null $null $crashes.Count
            $supervisorExit = 1
            break
        }
        $child = Find-ExistingServer
        if ($null -eq $child) {
            Archive-ServerLogs
            $launch = @{
                FilePath = $serverExecutable; WorkingDirectory = $serverDirectory; WindowStyle = 'Hidden'; PassThru = $true
                RedirectStandardOutput = (Join-Path $logDirectory 'server.stdout.log')
                RedirectStandardError = (Join-Path $logDirectory 'server.stderr.log')
            }
            if ($serverArguments.Count -gt 0) { $launch.ArgumentList = $serverArguments }
            $child = Start-Process @launch
            Write-SupervisorLog ('Started server PID ' + $child.Id + '.')
        } else {
            Write-SupervisorLog ('Adopted existing server PID ' + $child.Id + ' without interrupting it.')
        }
        # Retain the native handle before waiting so a quick exit cannot lose its exit code.
        $null = $child.Handle
        $childId = $child.Id
        Write-SupervisorState 'running' $childId $null $crashes.Count
        $child.WaitForExit()
        $child.Refresh()
        $exitCode = $child.ExitCode
        $child.Dispose()
        $child = $null
        if ($null -eq $exitCode) { throw 'The server exit code could not be read; refusing to mistake it for a shutdown.' }
        Write-SupervisorLog ('Server PID ' + $childId + ' exited with code ' + $exitCode + '.')
        if ($exitCode -eq 0) {
            Write-SupervisorState 'stopped' $null $exitCode $crashes.Count
            Write-SupervisorLog 'Intentional shutdown: leaving the server stopped.'
            break
        }
        if ($exitCode -eq 75) {
            Write-SupervisorState 'restarting' $null $exitCode $crashes.Count
            Start-Sleep -Seconds 2
            continue
        }
        $crashes = @($crashes) + @([datetime]::UtcNow)
        $times = @($crashes | ForEach-Object { $_.ToString('o') })
        [IO.File]::WriteAllText($crashFile, (ConvertTo-Json -InputObject $times), (New-Object Text.UTF8Encoding($false)))
        $delay = [Math]::Min(30, 5 * $crashes.Count)
        Write-SupervisorState 'recovering' $null $exitCode $crashes.Count
        Write-SupervisorLog ('Unexpected exit; trying again in ' + $delay + ' seconds.')
        Start-Sleep -Seconds $delay
    }
} catch {
    $supervisorExit = 1
    if ($owned) {
        Write-SupervisorLog ('Supervisor failed: ' + $_.Exception.Message)
        Write-SupervisorState 'faulted' $null $null
    }
    Write-Error $_ -ErrorAction Continue
} finally {
    if ($null -ne $child) { $child.Dispose() }
    if ($owned) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
exit $supervisorExit
