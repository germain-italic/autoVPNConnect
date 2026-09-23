# Isolated recovery checks

Run on Windows with Windows PowerShell 5.1, a .NET SDK and the .NET Framework 4.7.2
targeting pack, from the repository root:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/RecoveryChecks/Run.ps1
```

The script compiles a separate x86 console program without `SergiyE.Common`, Fody or
application startup. It uses the current `ConnectionManager.cs` and `RasManService.cs`
sources with a minimal settings substitute that never reads saved credentials. In a
temporary copy of `RasManService.cs`, only the task folder and wait timeout are changed:
the folder is unique to the invocation, and the timeout is three seconds instead of two
minutes. Substitutions fail explicitly if the expected declarations change.

The checks cover:

- Manual-disconnect state transitions with simulated network observations: a closing tunnel
  and stale samples cannot re-arm recovery, but an external connection after an observed
  disconnect does re-arm it even while the disconnect worker is busy. This uses the production
  transition method without subscribing to network events or dialing a VPN.
- The real `RasGetConnectStatus` P/Invoke with a null handle: Windows must return
  `ERROR_INVALID_HANDLE`, and `WaitForHangUp` must return without the fallback delay.
- The PowerShell action extracted from the generated task XML, with service cmdlets
  shadowed by functions in a separate process: success exits zero, a simulated restart
  failure exits nonzero, and both paths restore previously running dependents only.
- The real Task Scheduler and production `WaitForTaskCompletion` method: a harmless
  task exiting zero succeeds, one exiting seven reports that code, and one sleeping
  longer than the shortened wait budget times out. Each scenario gets a fresh task so
  that an earlier result cannot satisfy the next assertion.

The scheduler checks register tasks under `AutoVPNConnect-Probe-<random ID>` using the
current interactive user's token, without requesting elevation. Actions only exit or
sleep. Tasks are stopped and removed in `finally`, along with their folder; the build
directory is also removed. A forcibly terminated probe may leave its temporary folder
behind. The script never calls `RasDial`, `RasHangUp`, the real service restart methods,
or `AutoVPNConnect\RestartRasMan`.

If local policy denies task creation, the run fails visibly. To run only the RAS and
simulated-script checks, pass `-SkipScheduler`; the output explicitly reports the skip.
Do not describe such a run as validating Task Scheduler integration.

These checks do **not** validate UAC registration, the SYSTEM task's permissions, a real
service restart, the lifecycle of a live RAS handle, full error 633 recovery, or the UI.
In particular, the invalid-handle check does not prove that an active connection finishes
disconnecting correctly. The application runs since the dependency fixes described in the
root README, but these checks never start it: a passing run says nothing about the
application itself.

Check the UI by hand on a real build: edit the VPN name and username without saving, then
cause a status refresh (for example, changing the theme). The typed values must remain
intact; repeated refreshes must not add duplicate VPN names. Check a saved configuration and
a fresh configuration separately.
