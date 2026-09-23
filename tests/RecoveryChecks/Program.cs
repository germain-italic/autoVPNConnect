using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using AutoVPNConnect;

static class RecoveryChecks {
  const BindingFlags PrivateStatic = BindingFlags.NonPublic | BindingFlags.Static;

  static void Check(bool condition, string message) {
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS: " + message);
  }

  static object Call(Type type, string method, params object[] arguments) {
    return type.GetMethod(method, PrivateStatic).Invoke(null, arguments);
  }

  static int Main(string[] args) {
    try {
      Check(IntPtr.Size == 4, "probe runs as x86, matching the application's Prefer32Bit setting");
      CheckHangUp();
      CheckServiceScript();
      if (Array.IndexOf(args, "--skip-scheduler") >= 0)
        Console.WriteLine("SKIP: real Task Scheduler result checks (explicitly requested)");
      else
        CheckScheduler();
      return 0;
    }
    catch (Exception ex) {
      Console.Error.WriteLine(ex);
      return 1;
    }
  }

  static void CheckHangUp() {
    var type = typeof(ConnectionManager);
    var statusType = type.GetNestedType("RASCONNSTATUS", BindingFlags.NonPublic);
    var status = Activator.CreateInstance(statusType);
    statusType.GetField("dwSize").SetValue(status, Marshal.SizeOf(statusType));
    var arguments = new object[] { IntPtr.Zero, status };
    var result = (uint)Call(type, "RasGetConnectStatus", arguments);
    Check(result == 6, "real RasGetConnectStatus accepts the structure and reports ERROR_INVALID_HANDLE");
    var timer = Stopwatch.StartNew();
    Call(type, "WaitForHangUp", IntPtr.Zero);
    Check(timer.Elapsed < TimeSpan.FromSeconds(2), "hang-up wait returns promptly for an invalid handle");
    // Never call RasDial or RasHangUp: even a test must not disturb a live VPN.
  }

  static string Encoded(string script) {
    return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
  }

  static void CheckServiceScript() {
    var xml = new XmlDocument();
    xml.LoadXml((string)Call(typeof(RasManService), "BuildTaskXml"));
    var ns = new XmlNamespaceManager(xml.NameTable);
    ns.AddNamespace("t", "http://schemas.microsoft.com/windows/2004/02/mit/task");
    var arguments = xml.SelectSingleNode("//t:Exec/t:Arguments", ns).InnerText;
    const string marker = "-Command \"";
    var start = arguments.IndexOf(marker, StringComparison.Ordinal);
    Check(start >= 0 && arguments.EndsWith("\""), "task action can be extracted from generated XML");
    var command = arguments.Substring(start + marker.Length, arguments.Length - start - marker.Length - 1);
    foreach (var fail in new[] { false, true }) {
      // Functions shadow cmdlets in this child process. The production action is executed
      // unchanged, but every service operation is simulated, including a restart failure.
      var script = @"
$ProgressPreference = 'SilentlyContinue'
$script:restored = @()
$script:restarted = $false
function Get-Service { param($Name)
  if ($Name -ne 'RasMan') { throw 'Unexpected service' }
  [pscustomobject]@{ DependentServices = @(
    [pscustomobject]@{Name='ProbeRunning';Status='Running'},
    [pscustomobject]@{Name='ProbeStopped';Status='Stopped'}) }
}
function Restart-Service { param($Name, [switch]$Force)
  if ($Name -ne 'RasMan' -or -not $Force) { throw 'Unexpected restart' }
  $script:restarted = $true
  if ($script:failRestart) { throw 'Simulated restart failure' }
}
function Start-Service { param($Name) $script:restored += $Name }
" + "$script:failRestart = $" + (fail ? "true" : "false") + @"
try {
  try {
" + command + @"
  }
  finally {
    if (-not $script:restarted -or $script:restored.Count -ne 1 -or $script:restored[0] -ne 'ProbeRunning') {
      Write-Output 'ASSERTION FAILED'; exit 42
    }
    Write-Output 'DEPENDENTS RESTORED'
  }
}
catch { Write-Output $_; exit 1 }
";
      var info = new ProcessStartInfo("powershell.exe", "-NoProfile -NonInteractive -EncodedCommand " + Encoded(script)) {
        UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true,
        RedirectStandardError = true
      };
      using (var process = Process.Start(info)) {
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(15000)) {
          process.Kill();
          throw new Exception("Simulated service action timed out.");
        }
        if (process.ExitCode != (fail ? 1 : 0) || !stdout.Result.Contains("DEPENDENTS RESTORED"))
          throw new Exception("Unexpected simulated restart result: " + stdout.Result + stderr.Result);
        Check(true, "simulated restart " + (fail ? "failure propagates" : "succeeds") + " and restores only running dependents");
      }
    }
  }

  static void CheckScheduler() {
    var folderName = (string)typeof(RasManService).GetField("TaskFolder", PrivateStatic).GetRawConstantValue();
    Check(folderName.StartsWith("AutoVPNConnect-Probe-", StringComparison.Ordinal), "scheduler probe uses an isolated task folder");
    dynamic scheduler = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
    scheduler.Connect();
    dynamic root = scheduler.GetFolder("\\");
    dynamic folder = root.CreateFolder(folderName);
    try {
      foreach (var scenario in new[] { "success", "failure", "timeout" }) {
        dynamic definition = scheduler.NewTask(0);
        definition.RegistrationInfo.Description = "Temporary AutoVPNConnect recovery check; no service or VPN changes.";
        definition.Principal.LogonType = 3; // TASK_LOGON_INTERACTIVE_TOKEN; current user, no elevation.
        definition.Settings.DisallowStartIfOnBatteries = false;
        definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT30S";
        dynamic action = definition.Actions.Create(0);
        action.Path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell\\v1.0\\powershell.exe");
        action.Arguments = "-NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + Encoded(
          scenario == "timeout" ? "Start-Sleep -Seconds 15" : scenario == "failure" ? "exit 7" : "exit 0");
        dynamic task = folder.RegisterTaskDefinition("RestartRasMan", definition, 2, null, null, 3, null);
        try {
          var triggeredAt = DateTime.Now.AddSeconds(-2);
          task.Run(null);
          var values = new object[] { triggeredAt, null };
          var result = (bool)Call(typeof(RasManService), "WaitForTaskCompletion", values);
          var error = (string)values[1];
          Check(scenario == "success" ? result && error == null :
            !result && error != null && error.Contains(scenario == "failure" ? "code 7" : "did not finish in time"),
            "scheduler reports " + scenario + (error == null ? "" : ": " + error));
        }
        finally {
          try { task.Stop(0); }
          finally { folder.DeleteTask("RestartRasMan", 0); }
        }
      }
    }
    finally {
      root.DeleteFolder(folderName, 0);
    }
  }
}
