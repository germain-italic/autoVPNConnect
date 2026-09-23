using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

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
    private const string TaskFolder = "AutoVPNConnect";
    private const string TaskLeafName = "RestartRasMan";
    private const string TaskName = TaskFolder + "\\" + TaskLeafName;
    private const int TaskStateQueued = 2;
    private const int TaskStateRunning = 4;
    // Bump whenever the task's action changes. A task already registered is never replaced on
    // its own - schtasks /Create /F would overwrite it, but nothing asks it to - so without a
    // stamp an installation would quietly go on running the script it was first given.
    private const string TaskVersion = "2";
    private const string VersionPrefix = "[task v";
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(45);
    // Matches the task's own ExecutionTimeLimit: giving up sooner would report a failure for
    // a restart that is still running and about to succeed.
    private static readonly TimeSpan TaskTimeout = TimeSpan.FromMinutes(2);

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
    /// True when a helper task is registered but carries an older action than this build would
    /// install, so it should be replaced while the user is in front of the application.
    /// A task whose stamp cannot be read at all counts as current: guessing the other way
    /// would cost a UAC prompt every single time the window opens.
    /// </summary>
    public static bool IsTaskOutdated() {
      var version = ReadTaskVersion();
      return version != null && version != TaskVersion;
    }

    /// <returns>
    /// The version stamped in the registered task, an empty string when it carries none
    /// (anything registered before stamping existed), or null when the task could not be read.
    /// </returns>
    private static string ReadTaskVersion() {
      try {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null)
          return null;

        dynamic scheduler = Activator.CreateInstance(type);
        scheduler.Connect();
        string description = scheduler.GetFolder("\\" + TaskFolder)
          .GetTask(TaskLeafName).Definition.RegistrationInfo.Description;
        if (description == null)
          return string.Empty;

        var start = description.IndexOf(VersionPrefix, StringComparison.Ordinal);
        if (start < 0)
          return string.Empty;
        start += VersionPrefix.Length;
        var end = description.IndexOf(']', start);
        return end < 0 ? string.Empty : description.Substring(start, end - start);
      }
      catch {
        return null;
      }
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
            // Not "tick the box": the setting defaults to on, so it is already ticked and
            // toggling it is not what registers the task - opening the window is.
            error = "The helper that restarts the RasMan service is not set up yet. " +
              "Open the main window to finish the one-time setup.";
            return false;
          }
          if (!RegisterTask(out error))
            return false;
        }

        // A couple of seconds of slack: LastRunTime comes from the scheduler's clock.
        var triggeredAt = DateTime.Now.AddSeconds(-2);
        if (!RunSchTasks("/Run /TN \"" + TaskName + "\"", false, out var output)) {
          error = string.IsNullOrEmpty(output) ? "Could not start the RasMan recovery task." : output;
          return false;
        }

        if (!WaitForTaskCompletion(triggeredAt, out error))
          return false;

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

        // Only the ones we actually stopped are restored, and the loop is inside the try so a
        // dependent that refuses to stop cannot leave the ones before it down until reboot.
        var stopped = new List<ServiceController>();
        try {
          foreach (var dependent in service.DependentServices
                     .Where(d => d.Status != ServiceControllerStatus.Stopped)) {
            dependent.Stop();
            stopped.Add(dependent);
            dependent.WaitForStatus(ServiceControllerStatus.Stopped, StopTimeout);
          }

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
          foreach (var dependent in stopped) {
            try {
              dependent.Start();
              dependent.WaitForStatus(ServiceControllerStatus.Running, StartTimeout);
            }
            catch {
              // best effort: a dependent that refuses to come back must not fail the recovery
            }
          }
        }
      }
      SettleAfterRestart();
    }

    /// <summary>
    /// schtasks /Run only reports that the task was queued, so a restart that never ran would
    /// otherwise pass for a success. The scheduler itself is asked for the outcome: no file is
    /// written anywhere, which keeps a SYSTEM-owned write out of a directory that a standard
    /// user can pre-create, and no schtasks output has to be parsed in the user's language.
    /// </summary>
    private static bool WaitForTaskCompletion(DateTime triggeredAt, out string error) {
      error = null;
      try {
        var type = Type.GetTypeFromProgID("Schedule.Service");
        if (type == null) {
          error = "The Task Scheduler service is not available.";
          return false;
        }

        dynamic scheduler = Activator.CreateInstance(type);
        scheduler.Connect();
        dynamic task = scheduler.GetFolder("\\" + TaskFolder).GetTask(TaskLeafName);

        var deadline = DateTime.UtcNow + TaskTimeout;
        while (DateTime.UtcNow < deadline) {
          int state = task.State;
          if (state != TaskStateRunning && state != TaskStateQueued) {
            DateTime lastRun = task.LastRunTime;
            if (lastRun >= triggeredAt) {
              int result = task.LastTaskResult;
              if (result == 0)
                return true;
              error = "The RasMan recovery task failed with code " + result + ".";
              return false;
            }
          }
          Thread.Sleep(500);
        }

        error = "The RasMan recovery task did not finish in time.";
        return false;
      }
      catch (Exception ex) {
        error = "Could not read the result of the RasMan recovery task: " + ex.Message;
        return false;
      }
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

          // The elevated call waits on a UAC prompt, which is the user's own pace.
          if (!process.WaitForExit(elevated ? 300000 : 60000)) {
            try {
              process.Kill();
              process.WaitForExit(5000);
            }
            catch {
              //ignore, an elevated child cannot be killed from here anyway
            }
            output = "schtasks did not finish in time.";
            return false;
          }

          if (!elevated) {
            // Bounded: WaitForExit(int) does not close the redirected pipes, and a process
            // that passed a handle on keeps the write end open after exiting.
            Task.WaitAll(new Task[] { stdout, stderr }, 5000);
            output = (Text(stdout) + Text(stderr)).Trim();
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

    private static string Text(Task<string> reader) {
      return reader != null && reader.Status == TaskStatus.RanToCompletion ? reader.Result : string.Empty;
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

      // -ErrorAction Stop makes a failed restart a non-zero exit code, which is what the
      // caller reads back as the task's LastTaskResult.
      // Restart-Service -Force stops dependent services so the stop can proceed, but starts
      // only RasMan again - so they are noted first and put back afterwards, matching what
      // the elevated in-process path does. The restoration sits in a finally for the same
      // reason it does in C#: a restart that fails halfway has already stopped them, and
      // leaving it out of the failure path would strand them until the next reboot. The
      // error still propagates once the finally has run, so the exit code is unaffected.
      var command =
        "$ErrorActionPreference = 'Stop'; " +
        "$deps = @((Get-Service -Name RasMan).DependentServices | Where-Object { $_.Status -ne 'Stopped' }); " +
        "try { Restart-Service -Name RasMan -Force } " +
        "finally { foreach ($d in $deps) { try { Start-Service -Name $d.Name } catch { } } }";

      return
        "<?xml version=\"1.0\" encoding=\"UTF-16\"?>\r\n" +
        "<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">\r\n" +
        "  <RegistrationInfo>\r\n" +
        "    <Author>AutoVPNConnect</Author>\r\n" +
        "    <Description>Restarts the Windows RasMan service to release a stuck VPN port (RAS error 633). Created by AutoVPNConnect " + VersionPrefix + TaskVersion + "]</Description>\r\n" +
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
