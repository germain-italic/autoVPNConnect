using System;
using System.Diagnostics;
using System.IO;
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
    /// sees a clean set of ports. Returns false with a message when the restart could not run.
    /// </summary>
    public static bool TryRestart(out string error) {
      error = null;
      try {
        if (IsElevated) {
          RestartDirectly();
          return true;
        }

        if (!IsTaskRegistered() && !RegisterTask(out error))
          return false;

        if (!RunSchTasks("/Run /TN \"" + TaskName + "\"", false, out var output)) {
          error = string.IsNullOrEmpty(output) ? "Could not start the RasMan recovery task." : output;
          return false;
        }

        WaitForServiceRestart();
        return true;
      }
      catch (Exception ex) {
        error = ex.Message;
        return false;
      }
    }

    private static void RestartDirectly() {
      using (var service = new ServiceController(ServiceName)) {
        if (service.Status != ServiceControllerStatus.Stopped) {
          service.Stop();
          service.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
        }
        service.Start();
        service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
      }
      SettleAfterRestart();
    }

    /// <summary>
    /// The task runs asynchronously, so watch the service rather than the task. Missing the
    /// brief stop is not an error - the restart can simply be faster than we poll.
    /// </summary>
    private static void WaitForServiceRestart() {
      using (var service = new ServiceController(ServiceName)) {
        var deadline = DateTime.UtcNow + StopTimeout;
        while (DateTime.UtcNow < deadline) {
          service.Refresh();
          if (service.Status != ServiceControllerStatus.Running)
            break;
          Thread.Sleep(250);
        }
        service.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
      }
      SettleAfterRestart();
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
          if (!elevated)
            output = (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd()).Trim();
          if (!process.WaitForExit(60000)) {
            try {
              process.Kill();
            }
            catch {
              //ignore
            }
            return false;
          }
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
        "      <Arguments>-NoProfile -NonInteractive -WindowStyle Hidden -Command \"Restart-Service -Name RasMan -Force\"</Arguments>\r\n" +
        "    </Exec>\r\n" +
        "  </Actions>\r\n" +
        "</Task>\r\n";
    }
  }
}
