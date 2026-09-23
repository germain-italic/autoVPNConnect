# Auto VPN Connect
[![Release](https://img.shields.io/github/v/release/germain-italic/autoVPNConnect)](https://github.com/germain-italic/autoVPNConnect/releases/latest)
![Last commit](https://img.shields.io/github/last-commit/germain-italic/autoVPNConnect?color=00AD00)

AutoVPNConnect is free software that reconnects your VPN automatically when it drops.

This repository is a fork of [sergiye/autoVPNConnect](https://github.com/sergiye/autoVPNConnect)
by Sergiy Egoshyn, maintained by [germain-italic](https://github.com/germain-italic)
independently of the original project.

----

## Features

### What can it do?

 - Reconnect with saved user/password, through the Windows RAS API.
 - Reconnect without saved user/password by rasphone command (The VPN connection dialog box will be displayed.)
 - Disconnect on purpose: *Force disconnect* in the tray, or *Disconnect* in the window,
   holds until you connect again, even with *Restore lost connection* ticked.
 - Retry a failed reconnect on its own, with increasing delays, see below.
 - Detect a VPN that reads as connected but carries no traffic, by pinging a host behind it.
 - Tray notifications when the VPN drops, comes back or fails to reconnect.
 - Connection history in a standard JSON Lines log.
 - Recover from RAS error 633 (`the specified port is already open`) by restarting the RasMan service, see below.
 - Saved password protected with Windows DPAPI, tied to your user account.
 - *Check for updates* offers new releases of this fork; nothing installs without asking.
 - Runs as background application with tray icon.
 - `Light` / `Dark` themes with `Auto` mode to switch when changing system settings
 - No installation required, just save the executable file anywhere on your computer and run it.
 

### UI example 

Main app window:

[<img src="preview.png" alt="Preview" width="300"/>](preview.png)

Extended app system menu:

[<img src="sysMenu.png" alt="Preview" width="300"/>](sysMenu.png)

System tray integration with menu:

[<img src="sysTray.png" alt="Preview" width="300"/>](sysTray.png)

## Download

Builds of this fork are published on its
[releases](https://github.com/germain-italic/autoVPNConnect/releases) page. The in-app *Site*
and *Check for updates* entries point to this fork, not to the original project.

## Why this fork

As published, the original project does not build into a working program: the library it
needs resolves to a stub, and that library's declared source repository returns 404.

 - **The dependency is withheld.** `SergiyE.Common` and `SergiyE.Common.UI` are the author's
   own NuGet packages. The current version, 1.0.9745, is published as a stub: 470 methods of
   `SergiyE.Common` throw `NotImplementedException`, including the first one called at
   startup. An older version, 1.0.9500, still has real code; nothing in the project says so,
   and its `1.*` reference picks the stub.
 - **Its source cannot be found.** Both packages are labelled GPL-3.0-only and name
   `https://github.com/SergiyE/Common` as their project and repository. That link returns
   404, and no public repository of the `sergiye` account contains the library.

The original source builds, but the resulting executable cannot start. A second, separate
cause:

 - `Costura.Fody` 4.1.0, when the project is built with `dotnet build`, weaves references to
   .NET 8 (`System.Private.CoreLib 8.0`) into an executable that runs on .NET Framework 4.7.2.
   It fails before `Main` with a `FileNotFoundException`.

The first problem was reported upstream in
[issue #2](https://github.com/sergiye/autoVPNConnect/issues/2). It was closed as not planned,
with this reply from the maintainer:

> Your AI wasted my time having me read a long generated report that asked me to spend even
> more time modifying the project just to make it easier for the AI to work with.
> Don't do that.
> If you have complaints about how the program is working, describe them yourself.
> Clearly and concisely.
> Respect other people's time.

The reply addresses neither the stub package nor the missing source. Changes made in this
fork are therefore not offered upstream.

## What this fork changes

 - **Runs again.** Both packages are pinned to 1.0.9500, a version with real method bodies,
   and `Costura.Fody` is upgraded to 5.7.0. The OS compatibility check, absent from 1.0.9500,
   is dropped.
 - **RAS error 633 recovery**, described below.
 - **Links, update check and About box** point to this fork. The About box links to it.

### Bugs fixed from the original

Security:

 - **The VPN password was effectively stored in clear.** It was 3DES-encrypted with a key
   written in the public source code. It is now protected with Windows DPAPI, tied to the
   user account; a password saved by an earlier version is converted on first read.
 - **Windows' saved VPN credentials were copied into the app's settings**, under that same
   weak encryption, whenever the app had none of its own. They are now used for the dial
   only.
 - **Updates installed themselves.** Ten seconds after start, then daily, the updater
   downloaded the latest release, replaced the executable and restarted it, without asking.
   It now offers the update.
 - **Any TLS certificate was accepted.** The updater installed a process-wide callback that
   accepted every certificate, then downloaded and ran a replacement executable. The callback
   is removed at startup.
 - **The autostart entry was written unquoted.** With spaces in the path, Windows can run
   another executable first. It is now quoted; existing entries are still recognised.

Behaviour:

 - **A manual disconnect redialled at once.** Hanging up changes the network addresses, and
   with the restore option ticked, that change dialled straight back. Now a manual
   disconnect, *Force disconnect* in the tray, holds until the VPN is connected again.
 - **Ticking the restore option dialled.** *Restore lost connection*, formerly *Restore
   connection*, is now a setting only. The app still connects at startup when it is ticked.
 - **Status updates rewrote the settings fields**, so a network change duplicated the VPN
   entry or overwrote a user name being typed. The fields now load once.

### Fix stuck VPN port (RAS error 633)

 - **Symptom**: after a VPN drop, Windows keeps the WAN Miniport port in use. Every dial then
   fails with `Error 633: The port is already open` until RasMan restarts.
 - **Recovery**, option *Fix stuck VPN port*, on by default: hang up the leftover session and
   retry; if the port is still held, restart RasMan and retry once.
 - **Scope**: dials through the RAS API with saved credentials only, not the `rasphone` dialog.
 - **Side effect**: restarting RasMan drops every VPN and dial-up connection on the machine.
 - **Elevation**: a scheduled task, `AutoVPNConnect\RestartRasMan`, registered when the main
   window opens, with a single UAC prompt; the outcome is read back from Task Scheduler. When
   the app already runs elevated, RasMan is restarted directly.
 - **Updates**: the task is versioned; a release that changes it asks for UAC once more.
   Declining keeps the existing task.
 - **Removal**: untick the option, or
   `schtasks /Delete /TN "AutoVPNConnect\RestartRasMan" /F`.

### Retries, tunnel health and notifications

 - **Retries**: with *Restore lost connection* ticked, a failed reconnect is retried after
   30 s, then after a delay doubling up to 10 min, until it succeeds. A manual disconnect
   stops the retries. Retries never open the `rasphone` dialog: without saved credentials
   they stop, which the log records once, until the VPN connects or the settings are saved.
 - **Tunnel health**, option *Check that the VPN carries traffic*: pings the host you enter
   in *Ping host* every 30 s while connected. Pick a host that answers only through the VPN,
   such as an internal server; *Test* checks it, and warns when it also answers with the VPN
   down. After three missed pings the VPN is reconnected if *Restore lost connection* is
   ticked, otherwise you are notified once. If the host still does not answer after one
   reconnect, the app stops reconnecting for it and says so. Either way, the check acts
   again once the host answers.
 - **Notifications**, option *Show notifications*: tray balloons for a drop, the connection
   coming back (with the outage duration), a failed reconnect and an unresponsive tunnel.
   They need *Run in background*, which shows the tray icon they belong to.

### Connection log

Events are appended to `%LOCALAPPDATA%\AutoVPNConnect\connection.jsonl`, in
[JSON Lines](https://jsonlines.org): one JSON object per line, readable by `jq`, PowerShell's
`ConvertFrom-Json` or a log collector. *Connection log* in the tray menu opens it. The file
rotates at 1 MB into `connection.1.jsonl`.

Every line has `time` (RFC 3339, local time with offset), `level` (`info`, `warning`,
`error`) and `event`; other fields depend on the event:

```json
{"time":"2026-09-24T09:12:03.412+04:00","level":"warning","event":"connection_lost","connection":"Office VPN","connectedSeconds":5423}
{"time":"2026-09-24T09:12:34.020+04:00","level":"warning","event":"connect_failed","connection":"Office VPN","attempt":1,"error":"Error 800: ...","retryInSeconds":30}
{"time":"2026-09-24T09:13:05.871+04:00","level":"info","event":"connected","connection":"Office VPN","outageSeconds":62}
```

Events: `app_started`, `connected`, `disconnected` (manual or health check),
`connection_lost`, `connect_failed`, `retries_suspended`, `health_check_failed`, `health_check_recovered`,
`tunnel_unresponsive`, `health_check_inconclusive`, `port_stuck`, `rasman_restarted`,
`rasman_restart_failed`, `watchdog_error`.

```powershell
Get-Content $env:LOCALAPPDATA\AutoVPNConnect\connection.jsonl | ConvertFrom-Json |
  Where-Object event -eq connection_lost | Format-Table time, connectedSeconds
```

## Roadmap

 - **Drop the `SergiyE.Common` dependency.** It is published without public source, and
   its newer versions are stubs. Reimplementing the few parts in use (updater, settings
   storage, system menu helpers, themes) would also let the updater verify a release's
   SHA-256 before installing it.

## Contributing

Issues and pull requests are welcome on
[this fork](https://github.com/germain-italic/autoVPNConnect/issues).

### Building and validating changes

On Windows, with a .NET SDK and the .NET Framework 4.7.2 targeting pack:

```powershell
dotnet build -c Release
```

The executable is written to `AutoVPNConnect/bin/`. `make.bat` builds through Visual Studio's
MSBuild instead, when Visual Studio is installed.

The error 633 mechanisms can be checked on their own:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/RecoveryChecks/Run.ps1
```

See [the recovery checks](tests/RecoveryChecks/README.md) for prerequisites, coverage and
limits. These checks use an invalid RAS handle, simulated service commands and temporary
tasks with harmless actions. They never restart RasMan or run the application's recovery
task. Passing them verifies isolated mechanisms, not a complete VPN reconnection or the UI.

## License

The original code carries no license. It remains Copyright © 2014 Sergiy Egoshyn, all rights
reserved, and is published here under GitHub's terms of service, which allow viewing and
forking it on GitHub.

Changes made in this fork are licensed under the GNU General Public License v3.0, the
license of the `SergiyE.Common` packages embedded in the executable. See [LICENSE](LICENSE).
