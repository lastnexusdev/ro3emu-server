# Windows server supervision

The scripts in [`scripts/windows`](../../scripts/windows) make the panel's server controls work with a Windows
installation. Copy `Watch-Server.ps1`, `Install-Supervisor.ps1`, and `Start-Server.ps1` into the installation directory
containing `server/Ro3Emu.Server.exe`, alongside the launcher. These scripts are installation helpers, not a replacement
for the source-development runner.

Run `Install-Supervisor.ps1` from an administrator PowerShell. It registers the `UnspokenRo3-Supervisor` scheduled task
and starts it. The task runs as SYSTEM, without an interactive desktop or saved administrator password, twenty seconds
after Windows starts. It has no execution time limit and rejects concurrent task instances. An already running matching
server can be adopted without disconnecting players.

The supervisor interprets the server's exit code:

| Exit | Result |
|---|---|
| `75` | A panel restart, restore, update, or rollback relaunches the server after two seconds. |
| `0` | An intentional shutdown leaves the server stopped. Run `Start-Server.ps1` to start it again. |
| Other | An unexpected process exit is retried after a delay, increasing from five seconds to at most thirty. |

Five crashes within ten minutes pause automatic launches. After fixing the cause, run `Start-Server.ps1 -ResetFailures`.
The scheduled task retries a failure of the supervisor itself up to three times, one minute apart. It does not reboot
Windows or detect an operating-system hang. A Windows reboot starts the task again even after an intentional game-server
shutdown.

`Start-Server.ps1` always starts through the task and waits for its process-state record, preventing a manual start from
bypassing supervision. Prepare the exported game data before using these scripts on a dedicated server. The existing
installation launcher fallback exports from `C:\Games\Ragnarok3` only when `server/game-data-ready.txt` is absent; adapt
that path if the game is installed elsewhere.

Lifecycle events are written to `logs/server-supervisor.log`, with current process IDs and identity in
`logs/server-supervisor-state.json`. The current child keeps `server.stdout.log` and `server.stderr.log`. On relaunch,
these move to `logs/server-history`, retaining the most recent forty archived files. Keep runtime state and logs outside
version control.

The server's panel-schedule disposal is idempotent. Dependency injection can dispose the concrete service and its
interface alias separately; this must not turn the requested `75` or `0` into an unhandled-exception exit.
