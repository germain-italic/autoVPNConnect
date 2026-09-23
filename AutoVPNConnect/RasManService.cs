using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;

namespace AutoVPNConnect {

  /// <summary>
  /// Recovery for RAS error 633 (ERROR_PORT_ALREADY_OPEN).
  /// After a VPN session drops, Windows sometimes keeps the WAN Miniport port marked as in
  /// use. Every later dial then fails with 633 until the RasMan service is restarted, which
  /// no amount of retrying can fix on its own.
  /// Restarting a service requires elevation, so unless we already run elevated the work is
  /// delegated to a scheduled task registered once (a single UAC prompt) and then triggered
  /// silently - that is what keeps unattended reconnects working.
  /// </summary>
  static class RasManService {

    public const uint ErrorPortAlreadyOpen = 633;

    private const string ServiceName = "RasMan";
    private const string TaskName = "AutoVPNConnect\\RestartRasMan";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    // Matches the task's own ExecutionTimeLimit: giving up sooner would report a failure for
    // a restart that is still running and about to succeed.
    private static readonly TimeSpan StampTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The task writes this stamp after a successful restart. schtasks /Run only reports that
    /// the task was queued, so the stamp is the only trustworthy evidence that the restart
    /// actually ran - without it a task that fails to start looks like a success and the dial
    /// is retried for nothing. It lives under ProgramData so that the task (running as SYSTEM)
    /// can write it while the application (running as the user) can still read it.
    /// </summary>
    private static string StampPath {
      get {
        var dir = Path.Combine(
          Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "AutoVPNConnect");
        return Path.Combine(dir, "rasman-restart.stamp");
      }
    }

    public static bool IsElevated {
      get {
        try {
          using (var identity = WindowsIdentity.GetCurrent()) {
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
          }
        }
        catch {
          return false;
        }
      }
    }

    public static bool IsTaskRegistered() {
      return RunSchTasks("/Query /TN \"" + TaskName + "\"", false, out _);
    }

    /// <summary>
    /// Registers the elevated helper task. Shows a single UAC prompt, and returns false with
    /// a message if the user declines, so the caller can report why recovery did not happen.
    /// </summary>
    public static bool RegisterTask(out string error) {
      error = null;
      var xmlPath = Path.Combine(Path.GetTempPath(), "AutoVPNConnect.RestartRasMan.xml");
      try {
        // schtasks /XML only accepts a Unicode file.
        File.WriteAllText(xmlPath, BuildTaskXml(), new UnicodeEncoding(false, true));
        if (RunSchTasks("/Create /TN \"" + TaskName + "\" /XML \"" + xmlPath + "\" /F", true, out var output))
          return true;
        error = string.IsNullOrEmpty(output)
          ? "Could not register the RasMan recovery task (administrator rights are required)."
          : output;
        return false;
      }
      catch (Exception ex) {
        error = ex.Message;
        return false;
      }
      finally {
        try {
          if (File.Exists(xmlPath))
            File.Delete(xmlPath);
        }
        catch {
          //ignore
        }
      }
    }

    public static bool RemoveTask(out string error) {
      error = null;
      if (!IsTaskRegistered())
        return true;
      if (RunSchTasks("/Delete /TN \"" + TaskName + "\" /F", true, out var output))
        return true;
      error = string.IsNullOrEmpty(output) ? "Could not remove the RasMan recovery task." : output;
      return false;
    }

    /// <summary>
    /// Restarts RasMan and waits for it to come back up, so that a dial retried afterwards
    /// sees a clean set of ports. Returns false with a message when the restart did not run.
    /// </summary>
    /// <param name="allowRegistration">
    /// Whether a missing helper task may be registered here. Callers running in the background
    /// must pass false: RegisterTask raises a UAC prompt with no window to own it, which the
    /// user would never see while the application sits in the tray.
    /// </param>
    public static bool TryRestart(bool allowRegistration, out string error) {
      error = null;
      try {
        if (IsElevated) {
          RestartDirectly();
          return true;
        }

        if (!IsTaskRegistered()) {
          if (!allowRegistration) {
            error = "The helper that restarts the RasMan service is not set up yet. " +
              "Open the main window and tick 'Fix stuck VPN port' to allow it.";
            return false;
          }
          if (!RegisterTask(out error))
            return false;
        }

        var triggeredAt = DateTime.UtcNow;
        if (!RunSchTasks("/Run /TN \"" + TaskName + "\"", false, out var output)) {
          error = string.IsNullOrEmpty(output) ? "Could not start the RasMan recovery task." : output;
          return false;
        }

        if (!WaitForStamp(triggeredAt)) {
          error = "The RasMan recovery task did not report a successful restart.";
          return false;
        }

        SettleAfterRestart();
        return true;
      }
      catch (Exception ex) {
        error = ex.Message;
        return false;
      }
    }

    /// <summary>
    /// ServiceController.Stop on net472 has no stopDependentServices overload and throws when
    /// a dependent service is running, so dependents are stopped first and put back after.
    /// </summary>
    private static void RestartDirectly() {
      using (var service = new ServiceController(ServiceName)) {

        var dependents = service.DependentServices
          .Where(d => d.Status != ServiceControllerStatus.Stopped)
          .ToArray();

        foreach (var dependent in dependents) {
          dependent.Stop();
          dependent.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
        }

        try {
          if (service.Status != ServiceControllerStatus.Stopped) {
            service.Stop();
            service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
          }
          service.Start();
          service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
        }
        finally {
          // Whatever happened to RasMan, services we stopped ourselves must not be left down
          // until the next reboot.
          foreach (var dependent in dependents) {
            try {
              dependent.Start();
              dependent.WaitForStatus(ServiceControllerStatus.Running, StopTimeout);
            }
            catch {
              // best effort: a dependent that refuses to come back must not fail the recovery
            }
          }
        }
      }
      SettleAfterRestart();
    }

    private static bool WaitForStamp(DateTime triggeredAt) {
      var path = StampPath;
      var deadline = DateTime.UtcNow + StampTimeout;
      while (DateTime.UtcNow < deadline) {
        try {
          if (File.Exists(path) && File.GetLastWriteTimeUtc(path) >= triggeredAt)
            return true;
        }
        catch {
          //ignore, the task may be writing it right now
        }
        Thread.Sleep(500);
      }
      return false;
    }

    /// <summary>RasMan reports Running before its ports are usable again.</summary>
    private static void SettleAfterRestart() {
      Thread.Sleep(1500);
    }

    private static bool RunSchTasks(string arguments, bool elevated, out string output) {
      output = null;
      var startInfo = new ProcessStartInfo("schtasks.exe", arguments) {
        WindowStyle = ProcessWindowStyle.Hidden
      };
      if (elevated) {
        // Verb requires UseShellExecute, which rules out capturing the output.
        startInfo.UseShellExecute = true;
        startInfo.Verb = "runas";
      }
      else {
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
      }

      try {
        using (var process = Process.Start(startInfo)) {
          if (process == null)
            return false;

          // Both pipes have to be drained concurrently: reading one to the end while the other
          // fills its buffer would deadlock until the timeout below.
          var stdout = elevated ? null : process.StandardOutput.ReadToEndAsync();
          var stderr = elevated ? null : process.StandardError.ReadToEndAsync();

          if (!process.WaitForExit(60000)) {
            try {
              process.Kill();
              process.WaitForExit(5000);
            }
            catch {
              //ignore
            }
            output = "schtasks did not finish in time.";
            return false;
          }

          if (!elevated)
            output = (stdout.Result + stderr.Result).Trim();
          return process.ExitCode == 0;
        }
      }
      catch (System.ComponentModel.Win32Exception ex) {
        // 1223 = ERROR_CANCELLED, raised when the UAC prompt is dismissed.
        output = ex.NativeErrorCode == 1223
          ? "Administrator approval is required to set up the RasMan recovery task."
          : ex.Message;
        return false;
      }
      catch (Exception ex) {
        output = ex.Message;
        return false;
      }
    }

    private static string BuildTaskXml() {
      // Grant the current user the right to start the task, so triggering it later needs no
      // elevation. Without this, only an administrator could run it.
      var userSid = "BU";
      try {
        using (var identity = WindowsIdentity.GetCurrent()) {
          if (identity.User != null)
            userSid = identity.User.Value;
        }
      }
      catch {
        //ignore
      }

      // -ErrorAction Stop keeps the stamp from being written when the restart itself fails,
      // which is what lets the caller tell a real restart from a task that merely started.
      var stamp = StampPath.Replace("'", "''");
      // Restart-Service -Force stops dependent services so the stop can proceed, but starts
      // only RasMan again - so they are noted first and put back afterwards, matching what
      // the elevated in-process path does.
      var command =
        "$ErrorActionPreference = 'Stop'; " +
        "$deps = @((Get-Service -Name RasMan).DependentServices | Where-Object { $_.Status -ne 'Stopped' }); " +
        "Restart-Service -Name RasMan -Force; " +
        "foreach ($d in $deps) { try { Start-Service -Name $d.Name } catch { } }; " +
        "$p = '" + stamp + "'; " +
        "New-Item -ItemType Directory -Force -Path (Split-Path $p) | Out-Null; " +
        "Set-Content -Path $p -Value ([DateTime]::UtcNow.ToString('o'))";

      return
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
        "  <RegistrationInfo>\r\n" +
        "    <Author>AutoVPNConnect</Author>\r\n" +
        "    <Description>Restarts the Windows RasMan service to release a stuck VPN port (RAS error 633). Created by AutoVPNConnect.</Description>\r\n" +
        "    <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;FRFX;;;" + userSid + ")</SecurityDescriptor>\r\n" +
        "  </RegistrationInfo>\r\n" +
        "  <Triggers />\r\n" +
        "  <Principals>\r\n" +
        "    <Principal id=\"Author\">\r\n" +
        "      <UserId>S-1-5-18</UserId>\r\n" +
        "      <RunLevel>HighestAvailable</RunLevel>\r\n" +
        "    </Principal>\r\n" +
        "  </Principals>\r\n" +
        "  <Settings>\r\n" +
        "    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>\r\n" +
        "    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>\r\n" +
        "    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>\r\n" +
        "    <AllowHardTerminate>true</AllowHardTerminate>\r\n" +
        "    <StartWhenAvailable>false</StartWhenAvailable>\r\n" +
        "    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>\r\n" +
        "    <IdleSettings>\r\n" +
        "      <StopOnIdleEnd>false</StopOnIdleEnd>\r\n" +
        "      <RestartOnIdle>false</RestartOnIdle>\r\n" +
        "    </IdleSettings>\r\n" +
        "    <AllowStartOnDemand>true</AllowStartOnDemand>\r\n" +
        "    <Enabled>true</Enabled>\r\n" +
        "    <Hidden>false</Hidden>\r\n" +
        "    <RunOnlyIfIdle>false</RunOnlyIfIdle>\r\n" +
        "    <WakeToRun>false</WakeToRun>\r\n" +
        "    <ExecutionTimeLimit>PT2M</ExecutionTimeLimit>\r\n" +
        "    <Priority>7</Priority>\r\n" +
        "  </Settings>\r\n" +
        "  <Actions Context=\"Author\">\r\n" +
        "    <Exec>\r\n" +
        "      <Command>powershell.exe</Command>\r\n" +
        "      <Arguments>-NoProfile -NonInteractive -WindowStyle Hidden -Command \"" + command + "\"</Arguments>\r\n" +
        "    </Exec>\r\n" +
        "  </Actions>\r\n" +
        "</Task>\r\n";
    }
  }
}
