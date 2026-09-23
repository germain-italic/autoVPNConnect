# Auto VPN Connect
[![Release](https://img.shields.io/github/v/release/sergiye/AutoVPNConnect)](https://github.com/sergiye/AutoVPNConnect/releases/latest)
![Downloads](https://img.shields.io/github/downloads/sergiye/AutoVPNConnect/total?color=ff4f42)
![Last commit](https://img.shields.io/github/last-commit/sergiye/AutoVPNConnect?color=00AD00)

AutoVPNConnect - is free software that can auto reconnect your VPN if it was disconnected.

----

## Features

### What can it do?

 - Reconnect with saved user/password (by rasdial command).
 - Reconnect without saved user/password by rasphone command (The VPN connection dialog box will be displayed.)
 - Recover from RAS error 633 (`the specified port is already open`) by restarting the RasMan service, see below.
 - Runs as background application with tray icon.
 - `Light` / `Dark` themes with `Auto` mode to switch when changing system settings
 - No installation required, just save the executable file anywhere on your computer and run it.
 

### Fix stuck VPN port (RAS error 633)

After a VPN session drops, Windows sometimes keeps the WAN Miniport port marked as in use.
Every later dial then fails with `Error 633: The port is already open`, and no amount of
retrying helps - the RasMan service has to be restarted before the port is released. Left
alone, this silently defeats auto reconnect until someone notices and fixes it by hand.

With **Fix stuck VPN port** enabled (the default), a dial that fails with 633 first hangs up
the session left over from the dropped connection and retries; if the port is still held, it
restarts RasMan and retries once more.

Restarting a service needs administrator rights, so the application registers a scheduled
task, `AutoVPNConnect\RestartRasMan`, that performs the restart. It is registered the first
time you open the main window (or when you tick the option), never in the background while a
reconnect is failing - that is the only UAC prompt, and the task is triggered silently
afterwards, which is what keeps unattended reconnects working while the app sits in the tray.
Because `schtasks /Run` only reports that the task was queued, the outcome is read back from
the Task Scheduler itself - a restart that never ran would otherwise pass for a successful one.

If the application already runs elevated, no task is created and the service is restarted
directly, stopping and restoring any dependent services.

Unticking the option offers to remove the task. It can also be removed by hand:

```
schtasks /Delete /TN "AutoVPNConnect\RestartRasMan" /F
```

### UI example 

Main app window:

[<img src="https://github.com/sergiye/AutoVPNConnect/raw/master/preview.png" alt="Preview" width="300"/>](https://raw.githubusercontent.com/sergiye/AutoVPNConnect/master/preview.png)

Extended app system menu:

[<img src="https://github.com/sergiye/AutoVPNConnect/raw/master/sysMenu.png" alt="Preview" width="300"/>](https://raw.githubusercontent.com/sergiye/AutoVPNConnect/master/sysMenu.png)

System tray integration with menu:

[<img src="https://github.com/sergiye/AutoVPNConnect/raw/master/sysTray.png" alt="Preview" width="300"/>](https://raw.githubusercontent.com/sergiye/AutoVPNConnect/master/sysTray.png)

## Download

The published version can be obtained from [releases](https://github.com/sergiye/AutoVPNConnect/releases).

## How can I help improve it?
The AutoVPNConnect team welcomes feedback and contributions!<br/>
You can check if it works properly on your PC. If you notice any inaccuracies, please send us a pull request. 
If you have any suggestions or improvements, don't hesitate to create an issue.

Also, don't forget to star the repository to help other people find it.

## Donate!
Every [cup of coffee](https://patreon.com/SergiyE) you donate will help this app become better and let me know that this project is in demand.
