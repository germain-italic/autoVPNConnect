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

    // Hanging up is asynchronous: how long we are prepared to wait for the session to be gone,
    // how often we ask, and the blind wait Microsoft documents for when asking is not possible.
    private const int HangUpTimeoutMs = 5000;
    private const int HangUpPollMs = 100;
    private const int HangUpFallbackMs = 3000;
    private const uint ErrorInvalidHandle = 6;

    private const int RAS_MaxEntryName = 256;
    private const int UNLEN = 256;
    private const int PWLEN = 256;
    private const int DNLEN = 15;
    private const int RAS_MaxPhoneNumber = 128;
    private const int RAS_MaxCallbackNumber = RAS_MaxPhoneNumber;
    private const int RAS_MaxDeviceType = 16;
    private const int RAS_MaxDeviceName = 128;

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

    // Vista and later append the tunnel endpoints and the sub-state to RASCONNSTATUS; the
    // whole structure has to be described, because RasGetConnectStatus validates dwSize.
    [StructLayout(LayoutKind.Sequential)]
    struct RASTUNNELENDPOINT {
      public uint dwType;
      [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
      public byte[] addr;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    struct RASCONNSTATUS {
      public int dwSize;
      public int rasconnstate;
      public uint dwError;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxDeviceType + 1)]
      public string szDeviceType;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxDeviceName + 1)]
      public string szDeviceName;
      [MarshalAs(UnmanagedType.ByValTStr, SizeConst = RAS_MaxPhoneNumber + 1)]
      public string szPhoneNumber;
      public RASTUNNELENDPOINT localEndPoint;
      public RASTUNNELENDPOINT remoteEndPoint;
      public int rasconnsubstate;
    }

    [DllImport("rasapi32.dll", CharSet = CharSet.Auto)]
    private static extern uint RasDial(IntPtr lpRasDialExtensions, string lpszPhonebook, ref RASDIALPARAMS lpRasDialParams, int dwNotifierType, IntPtr lpvNotifier, out IntPtr lphRasConn);

    [DllImport("rasapi32.dll", SetLastError = true)]
    private static extern uint RasHangUp(IntPtr hRasConn);

    [DllImport("rasapi32.dll", CharSet = CharSet.Auto)]
    private static extern uint RasGetConnectStatus(IntPtr hRasConn, ref RASCONNSTATUS lpRasConnStatus);

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
      // A connection made outside the app (network flyout, rasphone) ends a manual
      // disconnect; otherwise that connection's next drop would never be restored. Not while
      // busy: the hang-up itself raises address changes while the tunnel is still up.
      if (manuallyDisconnected && !IsBusy && VpnIsConnected())
        manuallyDisconnected = false;
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
      var active = GetActiveVpnConnections(vpnConnectionName);
      // Match the configured connection rather than any PPP adapter that happens to be up:
      // an unrelated VPN would otherwise read as "connected" and hide our own failures.
      return string.IsNullOrEmpty(vpnConnectionName)
        ? active.Any()
        : active.Any(ni => ni.Name == vpnConnectionName);
    }

    /// <summary>
    /// Set when the user disconnects on purpose. Hanging up changes the network addresses,
    /// which fires NetworkAddressChanged, and with Reconnect ticked that dialled straight
    /// back: a manual disconnect was impossible. Automatic restores stay off until the VPN is
    /// connected again, from the app or elsewhere. Kept in memory only, so a restart of the
    /// app resumes them.
    /// </summary>
    private volatile bool manuallyDisconnected;

    public void ToggleConnection() {
      Task.Run(() => {
        if (VpnIsConnected()) {
          // Raised before hanging up: the address change arrives while the hang-up runs.
          manuallyDisconnected = true;
          var error = DisconnectFromVpn();
          if (error != null)
            manuallyDisconnected = false; // still connected, keep guarding against drops
          SetLastError(error);
        }
        else {
          manuallyDisconnected = false;
          SetLastError(ConnectToVpn());
        }
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
      var handle = hRasConn;
      HangUpStaleHandle();
      WaitForHangUp(handle);
      return true;
    }

    /// <summary>
    /// RasHangUp returns before the session is actually torn down, and dialling again too early
    /// earns another 633 - which would then cost a RasMan restart nobody needed, taking every
    /// other VPN on the machine down with it. Microsoft's remedy is to poll the handle until it
    /// is invalid; the fixed wait is the documented fallback for when polling is not possible.
    /// </summary>
    private static void WaitForHangUp(IntPtr handle) {
      var elapsed = Stopwatch.StartNew();
      while (elapsed.ElapsedMilliseconds < HangUpTimeoutMs) {
        uint ret;
        try {
          var status = new RASCONNSTATUS { dwSize = Marshal.SizeOf(typeof(RASCONNSTATUS)) };
          ret = RasGetConnectStatus(handle, ref status);
        }
        catch {
          ret = uint.MaxValue;
        }
        if (ret == ErrorInvalidHandle)
          return; // the session is gone, which is all we were waiting for
        if (ret != 0) {
          // The status call itself is unusable (a structure this build of Windows does not
          // recognise, say). Stop asking and fall back to waiting blind.
          Thread.Sleep(HangUpFallbackMs);
          return;
        }
        Thread.Sleep(HangUpPollMs);
      }
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
        // Bounded: WaitForExit(int) does not close the redirected pipes, and a process that
        // passed the handle on - rasphone launching the dialer - keeps the write end open
        // after exiting. An unbounded wait here would strand the dial slot for good.
        stdout?.Wait(5000);
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
        var exitCode = RunDialProcess(new ProcessStartInfo("rasdial.exe", $" \"{vpnName}\" /disconnect") {
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
          // Credentials read from Windows are used for this dial only. They used to be copied
          // into the app's settings, moving a password out of Windows' own protected store.
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
            if (recovery != null) {
              // The second dial may have handed back a handle of its own, and giving up here
              // must not leave it holding the port for the next attempt to trip over.
              HangUpStaleHandle();
              return recovery;
            }
            ret = Dial();
          }
          if (ret != 0) {
            HangUpStaleHandle(); // a failed dial must not be left holding the port
            return GetRasError(ret);
          }
          return null;
        }
        else {
          // Only reached without usable stored credentials, so the connection dialog is the
          // one option left. An unreachable rasdial arm that passed the password on the
          // command line, readable by any other process, was removed.
          var procStartInfo = new ProcessStartInfo("rasphone", " -d " + '"' + vpnName + '"') {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
          };
          // rasphone merely launches the connection dialog and returns, so its exit code is not
          // a RAS error and the 633 recovery has no meaning here - it lives in the RasDial branch.
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
      if (manuallyDisconnected)
        return;
      if (!VpnIsConnected() && mSettingsManager.IsConnectionConfigured) {
        SetLastError(ConnectToVpn());
      }
    }
  }
}
