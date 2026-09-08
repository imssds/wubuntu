# Wubuntu

A small Windows tray app that keeps your default Ubuntu distribution running in WSL without an open terminal. It checks that SSH responds inside Ubuntu, so you can use your already configured SSH client, including Codex.

Built with C# and Windows Forms on .NET Framework 4.8. Distributed as a ZIP, with no installer or Windows autostart.

## Before you start

You need:

- 64-bit Windows with .NET Framework 4.8 and working WSL.
- A fully initialized Ubuntu distribution set as the WSL default. Its registered name can be anything; Wubuntu checks the Linux distribution itself. Other Linux distributions are not supported.
- OpenSSH Server installed and configured to become available automatically when Ubuntu starts, through the SSH service or Ubuntu's `ssh.socket` activation.
- The standard Ubuntu tools `ss` (iproute2), `ssh-keyscan` (openssh-client), and `timeout` (coreutils) for readiness checks.
- Your SSH client, authentication, and network access already configured by you.

Wubuntu does not install WSL or Ubuntu, configure networking or authentication, or issue commands to start the SSH service. It uses the default Linux user for its keep-alive process. Readiness checks run as root inside the selected distribution to inspect listening sockets; they do not require Windows administrator privileges or a sudo password.

There are no Wubuntu settings to fill in. The app selects the WSL default distribution once at startup and keeps using it for that session, even if the WSL default changes later. No Linux username, SSH host, or port is hardcoded.

## Download and run

Download the ZIP from [GitHub Releases](https://github.com/imssds/wubuntu/releases), extract it to a writable folder, and run `Wubuntu.exe`. Keep the executable and its accompanying files together. You do not need to build the source.

The app appears in the Windows tray, sometimes under the overflow arrow beside the clock. It launches Ubuntu without opening a terminal and waits up to 15 seconds for SSH after the keep-alive process is ready.

## Using Wubuntu

Right-click the tray icon to see the selected distribution, status, and controls:

- **Starting** means Ubuntu is starting or Wubuntu is waiting for SSH.
- **Running** means Ubuntu is running and an SSH server inside that distribution answered a local readiness check. It does not verify your login, Windows or remote network access, or whether Codex is connected.
- **Restart WSL** stops the selected distribution and starts it again.
- **Exit** stops the selected distribution and closes the app.
- Click the status row to open `logs/session.log`.

**Restart WSL and Exit terminate all processes and SSH connections inside the selected distribution**, even if it was already running before Wubuntu opened. Other distributions are left alone.

## Errors and logs

If WSL is unavailable, the default distribution is missing or is not Ubuntu, or SSH does not become ready, Wubuntu records the error, shows an English Windows error dialog, and closes after you dismiss it. If Wubuntu started the distribution from a stopped state, it stops that distribution before showing the dialog. A distribution that was already running is left running after a failed startup.

If Ubuntu or SSH becomes unavailable after startup, the app shows **Error** in the tray and records the cause in the log, without a dialog or automatic recovery. After fixing your environment, use **Restart WSL** or reopen the app. A failed restart stays in the tray as Error.

Wubuntu creates `logs/session.log` beside the EXE. Each app session clears the previous log; the current log is capped at 1 MiB and retained on exit. Failure to write the log does not block the app or open an extra dialog. App messages are English; underlying system diagnostics retained in the log may use the operating system's language.

Each line starts with local Windows date and time to seconds, without milliseconds or a timezone suffix:

```text
2026-09-08 13:29:30 [INFO] Selected distribution: Ubuntu
2026-09-08 13:29:30 [INFO] Ubuntu is stopped
2026-09-08 13:29:30 [INFO] Starting Ubuntu
2026-09-08 13:29:38 [INFO] Ubuntu started
2026-09-08 13:29:38 [INFO] Waiting for SSH for up to 15 seconds
2026-09-08 13:29:39 [INFO] SSH is available at 127.0.0.1:2222
2026-09-08 13:29:39 [STATE] Running
```

`INFO` records actions and check results, `STATE` records app state changes, `ERROR` gives the failure reason, and `DETAIL` preserves technical diagnostics on separate timestamped lines. The log includes Restart and Exit requests, startup cleanup decisions, and successful full health checks once per minute while Running. Routine ten-second process checks and failed attempts during the initial SSH wait stay silent. If SSH never becomes ready, one final error includes the last attempt's diagnostic details when available.

## Build

From the repository root, run in PowerShell:

```powershell
.\source\build.ps1
.\dist\Wubuntu.exe
```

The build uses the .NET Framework C# compiler included with Windows; no external packages are needed. It writes `Wubuntu.exe`, `Wubuntu.exe.config`, `Wubuntu.ico`, and `LICENSE` to `dist/`. Close a running copy from that folder before rebuilding.

## Tests

Build with a temporary scratch folder, then pass that folder to the test script:

```powershell
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('Wubuntu-tests-' + [Guid]::NewGuid().ToString('N'))
try {
    .\source\build.ps1 -ScratchDirectory $scratch
    .\source\test.ps1 -ScratchDirectory $scratch
} finally {
    if (Test-Path -LiteralPath $scratch) {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
```

Tests use a fake WSL backend and a controllable clock for startup deadlines. The optional `-LiveCheck` switch starts the real default distribution and checks SSH inside it. It releases the keep-alive afterward without terminating the distribution; it never exercises Restart or Exit against your Linux workloads.

## Release ZIP

After building, package only the release files:

```powershell
Compress-Archive -LiteralPath .\dist\Wubuntu.exe, .\dist\Wubuntu.exe.config, .\dist\Wubuntu.ico, .\dist\LICENSE -DestinationPath .\dist\Wubuntu.zip -Force
```

Publish that ZIP in GitHub Releases. Do not include `logs/` or build/test scratch files. `source/` contains the code and scripts; `assets/` contains the original artwork. Build output in `dist/` is excluded from Git.

## License

[MIT](LICENSE).
