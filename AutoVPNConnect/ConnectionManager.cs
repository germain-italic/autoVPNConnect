using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AutoVPNConnect {

  class ConnectionManager {

    private const string BusyResult = "Busy";
    private const int HangUpSettleMs = 1500;

    private const int RAS_MaxEntryName = 256;
    private const int UNLEN = 256;
    private const int PWLEN = 256;
    private const int DNLEN = 15;
    private const int RAS_MaxPhoneNumber = 128;
    private const int RAS_MaxCallbackNumber = RAS_MaxPhoneNumber;

    private IntPtr hRasConn = IntPtr.Zero;
    private int busyFlag;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct RASDIALPARAMS {
      public int dwSize;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxEntryName + 1)]
      public string szEntryName;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxPhoneNumber + 1)]
      public string szPhoneNumber;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxCallbackNumber + 1)]
      public string szCallbackNumber;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = UNLEN + 1)]
      public string szUserName;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = PWLEN + 1)]
      public string szPassword;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = DNLEN + 1)]
      public string szDomain;
    }

    [DllImport("rasapi32.dll", CharSet = CharSet.Auto)]
    private static extern uint RasDial(IntPtr lpRasDialExtensions, string lpszPhonebook, ref RASDIALPARAMS lpRasDialParams, int dwNotifierType, IntPtr lpvNotifier, out IntPtr lphRasConn);

    [DllImport("rasapi32.dll", SetLastError = true)]
    private static extern uint RasHangUp(IntPtr hRasConn);

    [DllImport("rasapi32.dll", CharSet = CharSet.Auto)]
    private static extern uint RasGetErrorString(uint errorCode, StringBuilder lpszErrorString, int cBufSize);

    [DllImport("rasapi32.dll", CharSet = CharSet.Auto)]
    private static extern uint RasGetEntryDialParams(string lpszPhonebook, ref RASDIALPARAMS lpRasDialParams, ref bool lpfPassword);

    readonly SettingsManager mSettingsManager;

    public ConnectionManager(ref SettingsManager rSettingsManager) {
      mSettingsManager = rSettingsManager;
      NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
    }

    public event Action OnStatusChanged;

    private void NetworkAddressChanged(object sender, EventArgs e) {
      if (mSettingsManager.Reconnect) {
        // Windows delivers this on a shared notification thread; a reconnect can now take a
        // service restart's worth of time, so it must not run inline.
        Task.Run(() => RestoreConnection());
      }
      OnStatusChanged?.Invoke();
    }

    public bool IsBusy => Volatile.Read(ref busyFlag) != 0;

    /// <summary>
    /// Claims the single dial slot. Network change notifications are dispatched to the thread
    /// pool now, so a burst of them can run several attempts at once; two concurrent dials
    /// collide on the same port, which yields a self-inflicted 633 and, worse, a machine-wide
    /// service restart to "fix" it. A plain bool check-then-set does not prevent that.
    /// </summary>
    private bool TryBeginBusy() {
      if (Interlocked.CompareExchange(ref busyFlag, 1, 0) != 0)
        return false;
      OnStatusChanged?.Invoke();
      return true;
    }

    private void EndBusy() {
      Volatile.Write(ref busyFlag, 0);
      OnStatusChanged?.Invoke();
    }

    public static IEnumerable<NetworkInterface> GetActiveVpnConnections(string connectionName = null) {
      if (!NetworkInterface.GetIsNetworkAvailable())
        yield break;

      var interfaces = NetworkInterface.GetAllNetworkInterfaces();
      foreach (var ni in interfaces) {
        if (ni.NetworkInterfaceType == NetworkInterfaceType.Ppp &&
            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            ni.OperationalStatus == OperationalStatus.Up) {
          yield return ni;
          if (!string.IsNullOrEmpty(connectionName) && ni.Name == connectionName)
            yield break;
        }
      }
    }

    public bool VpnIsConnected() {
      var vpnConnectionName = mSettingsManager.VpnConnectionName;
      return GetActiveVpnConnections(vpnConnectionName).Any();
      //todo: hRasConn = IntPtr.Zero;
    }

    public void ToggleConnection() {
      Task.Run(() => {
        SetLastError(VpnIsConnected() ? DisconnectFromVpn() : ConnectToVpn());
      });
    }

    /// <summary>
    /// Why the last attempt failed, or null when it succeeded. Without this the result of
    /// ConnectToVpn was dropped on the floor and failures stayed invisible.
    /// </summary>
    public string LastError { get; private set; }

    private void SetLastError(string error) {
      if (error == BusyResult)
        return; // another attempt is already running, not a failure of this one
      LastError = error;
      OnStatusChanged?.Invoke();
    }

    private string GetRasError(uint errorCode) {
      var sb = new StringBuilder(512);
      // RasGetErrorString only knows RAS codes, and leaves the buffer untouched for anything
      // else - which matters now that raw process exit codes are reported through here.
      if (RasGetErrorString(errorCode, sb, sb.Capacity) == 0 && sb.Length > 0)
        return $"Error {errorCode}: {sb}";
      try {
        return $"Error {errorCode}: {new Win32Exception((int)errorCode).Message}";
      }
      catch {
        return $"Error {errorCode}";
      }
    }

    /// <summary>
    /// Hangs up the session left over from a dropped connection. That session is the cheapest
    /// explanation for a port Windows still believes is open, and releasing it avoids both a
    /// leaked handle and a service restart that would drop every other VPN on the machine.
    /// Returns true when something was actually released and the dial is worth retrying.
    /// </summary>
    private bool ReleaseStaleHandle() {
      if (hRasConn == IntPtr.Zero)
        return false;
      HangUpStaleHandle();
      Thread.Sleep(HangUpSettleMs); // RasHangUp returns before the port is actually free
      return true;
    }

    /// <summary>Same, without the settle delay, for when no retry follows.</summary>
    private void HangUpStaleHandle() {
      if (hRasConn == IntPtr.Zero)
        return;
      RasHangUp(hRasConn);
      hRasConn = IntPtr.Zero;
    }

    /// <summary>
    /// Handles RAS error 633 (ERROR_PORT_ALREADY_OPEN) once hanging up was not enough:
    /// Windows still believes the WAN Miniport port is in use, and only a RasMan restart
    /// releases it. Returns null when the caller should retry the dial, or a message
    /// explaining why recovery was not possible.
    /// </summary>
    private string ClearStuckPort() {
      var portError = GetRasError(RasManService.ErrorPortAlreadyOpen);

      if (!mSettingsManager.FixStuckPort)
        return portError + " Enable 'Fix stuck VPN port' in the settings to restart the RasMan service automatically.";

      // allowRegistration: false - we are on a background thread with no window to own a UAC
      // prompt. If the helper task is missing, say so instead of raising one behind the user's
      // back; opening the main window registers it.
      return RasManService.TryRestart(false, out var restartError)
        ? null
        : portError + " " + restartError;
    }

    private static int RunDialProcess(ProcessStartInfo startInfo) {
      using (var process = Process.Start(startInfo)) {
        // Drain the redirected pipe, otherwise a chatty rasdial can fill it and block until
        // the timeout this is guarding against.
        var stdout = startInfo.RedirectStandardOutput ? process.StandardOutput.ReadToEndAsync() : null;
        if (!process.WaitForExit(60000)) {
          try {
            process.Kill();
            process.WaitForExit(5000);
          }
          catch {
            //ignore
          }
          throw new TimeoutException("The dial command did not finish within 60 seconds.");
        }
        stdout?.Wait(); // the process has exited, so the reader completes
        return process.ExitCode;
      }
    }

    private string DisconnectFromVpn() {
      if (!TryBeginBusy()) {
        return BusyResult;
      }
      try {
        if (hRasConn != IntPtr.Zero) {
          uint ret = RasHangUp(hRasConn);
          hRasConn = IntPtr.Zero;
          if (ret == 0) {
            return null;
          }
          //else {
          //  return GetRasError(ret);
          //}
        }

        var vpnName = mSettingsManager.VpnConnectionName;
        var exitCode = RunDialProcess(new ProcessStartInfo("rasdial.exe", $" \u0022{vpnName}\u0022 /disconnect") {
          RedirectStandardOutput = true,
          UseShellExecute = false,
          CreateNoWindow = true,
          WindowStyle = ProcessWindowStyle.Hidden
        });
        return exitCode == 0 ? null : GetRasError((uint)exitCode);
      }
      catch (Exception ex) {
        return ex.Message;
      }
      finally {
        EndBusy();
      }
    }

    private string ConnectToVpn() {
      if (!TryBeginBusy()) return BusyResult;
      // Only once the slot is ours: clearing it before would wipe the error belonging to the
      // attempt that is already running. The UI hides it meanwhile, since we now count as busy.
      LastError = null;
      try {
        var vpnName = mSettingsManager.VpnConnectionName;
        var userName = mSettingsManager.UserName;
        var password = mSettingsManager.Password;

        var dialParams = new RASDIALPARAMS();
        dialParams.dwSize = Marshal.SizeOf(typeof(RASDIALPARAMS));
        dialParams.szEntryName = vpnName;
        dialParams.szUserName = userName;
        dialParams.szPassword = password;
        dialParams.szDomain = "";

        bool hasPassword = !string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password);
        if (!hasPassword) {
          var ret = RasGetEntryDialParams(null, ref dialParams, ref hasPassword);
          if (ret != 0)
            return GetRasError(ret);
        }
        if (hasPassword && !string.IsNullOrEmpty(dialParams.szUserName)) {
          mSettingsManager.UserName = dialParams.szUserName;
          mSettingsManager.Password = dialParams.szPassword;
          // RasDial hands back a usable handle even when it fails, and that half-open session
          // is itself a reason the port stays busy. Adopting it here is what lets the cheap
          // remedy below work on the very first dial after the application starts.
          uint Dial() {
            var code = RasDial(IntPtr.Zero, null, ref dialParams, 0, IntPtr.Zero, out var handle);
            if (handle != IntPtr.Zero)
              hRasConn = handle;
            return code;
          }

          var ret = Dial();
          // Two remedies, cheapest first, each tried at most once.
          if (ret == RasManService.ErrorPortAlreadyOpen && ReleaseStaleHandle())
            ret = Dial();
          if (ret == RasManService.ErrorPortAlreadyOpen) {
            var recovery = ClearStuckPort();
            if (recovery != null)
              return recovery;
            ret = Dial();
          }
          if (ret != 0) {
            HangUpStaleHandle(); // a failed dial must not be left holding the port
            return GetRasError(ret);
          }
          return null;
        }
        else {
          ProcessStartInfo procStartInfo;
          if (!string.IsNullOrEmpty(userName) && !string.IsNullOrEmpty(password)) {
            var rasdialCommand = " " + '\u0022' + vpnName + '\u0022';
            rasdialCommand += " " + userName;
            rasdialCommand += " " + password;
            procStartInfo = new ProcessStartInfo("rasdial.exe", rasdialCommand);
          }
          else {
            var rasphoneCommand = " -d " + '\u0022' + vpnName + '\u0022';
            procStartInfo = new ProcessStartInfo("rasphone", rasphoneCommand);
          }

          procStartInfo.RedirectStandardOutput = true;
          procStartInfo.UseShellExecute = false;
          procStartInfo.CreateNoWindow = true;
          procStartInfo.WindowStyle = ProcessWindowStyle.Hidden;
          // This branch is only reached without usable stored credentials, which means the
          // rasdial arm above it is unreachable and rasphone always wins. rasphone merely
          // launches the connection dialog and returns, so its exit code is not a RAS error
          // and the 633 recovery has no meaning here - it lives in the RasDial branch.
          var exitCode = RunDialProcess(procStartInfo);
          return exitCode == 0 ? null : $"Error {exitCode}";
        }
      }
      catch (Exception ex) {
        return ex.Message;
      }
      finally {
        EndBusy();
      }
    }

    public void RestoreConnection() {
      if (!VpnIsConnected() && mSettingsManager.IsConnectionConfigured) {
        SetLastError(ConnectToVpn());
      }
    }
  }
}
