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
    private const int TunnelDownTimeoutMs = 5000;
    private const uint ErrorInvalidHandle = 6;

    // Restores used to be tried only on a network change, so one failure (server down, Wi-Fi
    // not ready yet) left the VPN down until the next change, possibly for hours. The watchdog
    // retries: first after 30 s, then doubling up to every 10 minutes.
    private const int WatchdogIntervalMs = 15000;
    private static readonly TimeSpan FirstRetryDelay = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(10);

    // An interface can read as up while the tunnel carries nothing. A ping every 30 s to a
    // host behind the VPN tells; three misses in a row count as a dead tunnel.
    private static readonly TimeSpan HealthCheckInterval = TimeSpan.FromSeconds(30);
    private const int HealthCheckTimeoutMs = 3000;
    private const int HealthCheckFailureLimit = 3;

    private const string NoCredentialsForRetry =
      "No saved credentials: automatic retries cannot open the connection dialog.";

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

    private readonly Timer watchdog;

    public ConnectionManager(ref SettingsManager rSettingsManager) {
      mSettingsManager = rSettingsManager;
      NetworkChange.NetworkAddressChanged += NetworkAddressChanged;
      watchdog = new Timer(_ => WatchdogTick(), null, WatchdogIntervalMs, WatchdogIntervalMs);
    }

    public event Action OnStatusChanged;

    /// <summary>Something the user should hear about: the text, and whether it is bad news.</summary>
    public event Action<string, bool> OnNotify;

    private string ConnectionName => mSettingsManager.VpnConnectionName;

    private void NetworkAddressChanged(object sender, EventArgs e) {
      // A connection made outside the app (network flyout, rasphone) ends a manual
      // disconnect; otherwise that connection's next drop would never be restored. The tunnel
      // must first have been seen down: while the hang-up completes the interface still reads
      // as up, and taking that for a new connection let the drop that follows redial.
      // Sampling the interface takes a while and happens outside the lock, so the sample can
      // be stale by the time it is used. It only counts as a new connection if the tunnel was
      // already known down before sampling began, and no other manual disconnect started since.
      long generation;
      bool downBeforeSample;
      lock (manualDisconnectLock) {
        generation = disconnectGeneration;
        downBeforeSample = tunnelDownSinceManualDisconnect;
      }
      var connected = VpnIsConnected();
      RecordNetworkState(generation, downBeforeSample, connected);
      TrackState();
      if (mSettingsManager.Reconnect) {
        // Windows delivers this on a shared notification thread; a reconnect can now take a
        // service restart's worth of time, so it must not run inline.
        Task.Run(() => RestoreConnection());
      }
      OnStatusChanged?.Invoke();
    }

    private void RecordNetworkState(long generation, bool downBeforeSample, bool connected) {
      lock (manualDisconnectLock) {
        if (manuallyDisconnected && generation == disconnectGeneration) {
          if (!connected)
            tunnelDownSinceManualDisconnect = true;
          // Once the old tunnel was observed down, this is a new connection even if the
          // disconnect worker is still waiting. Discarding it while busy would leave manual
          // suppression armed at the next drop. The dial slot still prevents concurrent work.
          else if (downBeforeSample)
            manuallyDisconnected = false;
        }
      }
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

    /// <summary>
    /// Whether the tunnel has been seen down since the manual disconnect. Only then can seeing
    /// it up again mean a new connection rather than the old one still closing.
    /// </summary>
    private volatile bool tunnelDownSinceManualDisconnect;

    private readonly object manualDisconnectLock = new object();

    /// <summary>Counts manual disconnects, so a sample taken during an earlier one is ignored.</summary>
    private long disconnectGeneration;

    public void ToggleConnection() {
      Task.Run(() => {
        if (VpnIsConnected()) {
          // Raised before hanging up: the address change arrives while the hang-up runs.
          lock (manualDisconnectLock) {
            disconnectGeneration++;
            tunnelDownSinceManualDisconnect = false;
            manuallyDisconnected = true;
          }
          var error = DisconnectFromVpn();
          if (error != null)
            manuallyDisconnected = false; // still connected, keep guarding against drops
          SetLastError(error);
        }
        else {
          manuallyDisconnected = false;
          var error = ConnectToVpn();
          RecordAttempt(error);
          SetLastError(error);
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

      ConnectionLog.Warning("port_stuck", new { connection = ConnectionName, fixEnabled = mSettingsManager.FixStuckPort });
      if (!mSettingsManager.FixStuckPort)
        return portError + " Enable 'Fix stuck VPN port' in the settings to restart the RasMan service automatically.";

      // allowRegistration: false - we are on a background thread with no window to own a UAC
      // prompt. If the helper task is missing, say so instead of raising one behind the user's
      // back; opening the main window registers it.
      if (RasManService.TryRestart(false, out var restartError)) {
        ConnectionLog.Info("rasman_restarted");
        return null;
      }
      ConnectionLog.Error("rasman_restart_failed", new { error = restartError });
      return portError + " " + restartError;
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
        var error = HangUp();
        // Still busy here: wait for the interface to read as down, so the closing tunnel is
        // never taken for a new connection, and record that it went down. If it never does,
        // the manual disconnect simply stays in force.
        if (error == null && WaitForTunnelDown()) {
          lock (manualDisconnectLock)
            tunnelDownSinceManualDisconnect = true;
        }
        return error;
      }
      finally {
        EndBusy();
      }
    }

    /// <summary>
    /// The interface can outlive the RAS session by a moment, which is what made a closing
    /// tunnel look like a fresh connection. Returns false if it still reads as up at the end.
    /// </summary>
    private bool WaitForTunnelDown() {
      var elapsed = Stopwatch.StartNew();
      while (VpnIsConnected()) {
        if (elapsed.ElapsedMilliseconds >= TunnelDownTimeoutMs)
          return false;
        Thread.Sleep(HangUpPollMs);
      }
      return true;
    }

    private string HangUp() {
      try {
        if (hRasConn != IntPtr.Zero) {
          var handle = hRasConn;
          uint ret = RasHangUp(handle);
          hRasConn = IntPtr.Zero;
          if (ret == 0) {
            // RasHangUp returns before the session is gone. Stay busy until it is, so the
            // closing tunnel is not taken for a connection and nothing dials into it.
            WaitForHangUp(handle);
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
    }

    /// <param name="allowDialog">
    /// False for the watchdog's retries: without stored credentials the fallback is the
    /// rasphone dialog, and popping it up every few minutes would be worse than waiting.
    /// </param>
    private string ConnectToVpn(bool allowDialog = true) {
      if (!TryBeginBusy()) return BusyResult;
      // Only once the slot is ours: clearing it before would wipe the error belonging to the
      // attempt that is already running. The UI hides it meanwhile, since we now count as busy.
      var previousError = LastError;
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
          if (!allowDialog) {
            LastError = previousError; // nothing was attempted; keep what the user last saw
            return NoCredentialsForRetry;
          }
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

    public void RestoreConnection(bool allowDialog = true) {
      if (manuallyDisconnected)
        return;
      if (!VpnIsConnected() && mSettingsManager.IsConnectionConfigured) {
        var error = ConnectToVpn(allowDialog);
        if (error == NoCredentialsForRetry) {
          SuspendRetriesForCredentials();
          return; // not a failure of the connection, and not worth replacing the real error
        }
        RecordAttempt(error);
        SetLastError(error);
      }
    }

    #region retries

    private readonly object retryLock = new object();
    private int failedAttempts;
    private DateTime nextRetryAt = DateTime.MinValue;

    // Without saved credentials every automatic retry is bound to fail. Stop retrying, say so
    // once, and wait for something that can change it: a connection, or settings being saved.
    private bool retriesNeedCredentials;

    private void SuspendRetriesForCredentials() {
      lock (retryLock) {
        if (retriesNeedCredentials)
          return;
        retriesNeedCredentials = true;
      }
      ConnectionLog.Warning("retries_suspended", new { connection = ConnectionName, reason = "no_saved_credentials" });
    }

    /// <summary>Back to a first retry after 30 s, e.g. once the VPN is up again or settings change.</summary>
    public void ResetRetrySchedule() {
      lock (retryLock) {
        failedAttempts = 0;
        nextRetryAt = DateTime.MinValue;
        retriesNeedCredentials = false;
      }
    }

    /// <summary>Feeds the retry schedule: a success resets it, a failure pushes it back.</summary>
    private void RecordAttempt(string error) {
      if (error == BusyResult)
        return; // another attempt was running; its own outcome counts
      if (error == null) {
        ResetRetrySchedule();
        return;
      }
      lock (retryLock) {
        failedAttempts++;
        var doublings = Math.Min(failedAttempts - 1, 10);
        var delay = TimeSpan.FromTicks(Math.Min(MaxRetryDelay.Ticks, FirstRetryDelay.Ticks << doublings));
        nextRetryAt = DateTime.Now + delay;
        var retries = mSettingsManager.Reconnect && !manuallyDisconnected;
        ConnectionLog.Warning("connect_failed", new {
          connection = ConnectionName,
          attempt = failedAttempts,
          error,
          retryInSeconds = retries ? (int?)delay.TotalSeconds : null,
        });
      }
    }

    private bool IsRetryDue() {
      lock (retryLock)
        return !retriesNeedCredentials && DateTime.Now >= nextRetryAt;
    }

    #endregion

    #region watchdog

    private int watchdogRunning;

    private void WatchdogTick() {
      // A slow ping or dial must not let ticks pile up behind it.
      if (Interlocked.Exchange(ref watchdogRunning, 1) != 0)
        return;
      try {
        TrackState();
        if (IsBusy || !mSettingsManager.IsConnectionConfigured)
          return;
        if (VpnIsConnected())
          CheckTunnelHealth();
        else if (mSettingsManager.Reconnect && !manuallyDisconnected && IsRetryDue())
          RestoreConnection(allowDialog: false);
      }
      catch (Exception ex) {
        ConnectionLog.Error("watchdog_error", new { error = ex.Message });
      }
      finally {
        Volatile.Write(ref watchdogRunning, 0);
      }
    }

    #endregion

    #region state tracking

    private readonly object stateLock = new object();
    private bool? lastConnected;
    private DateTime connectedAt;
    private DateTime? lostAt; // set only for drops the user did not ask for

    /// <summary>
    /// Logs and announces transitions. Called on every network change and watchdog tick,
    /// so a drop that raised no event is still noticed within one tick.
    /// </summary>
    private void TrackState() {
      var connected = VpnIsConnected();
      string bad = null, good = null;
      lock (stateLock) {
        if (lastConnected == connected)
          return;
        var wasConnected = lastConnected == true;
        lastConnected = connected;
        var now = DateTime.Now;
        if (connected) {
          connectedAt = now;
          // However it came back, the next drop starts a fresh schedule: 30 s, not whatever
          // an earlier run of failures had reached.
          ResetRetrySchedule();
          var outage = now - lostAt;
          lostAt = null;
          ConnectionLog.Info("connected", new { connection = ConnectionName, outageSeconds = (int?)outage?.TotalSeconds });
          if (outage.HasValue)
            good = $"VPN connection restored after {Describe(outage.Value)}.";
        }
        else if (wasConnected) {
          var uptime = (int)(now - connectedAt).TotalSeconds;
          if (manuallyDisconnected) {
            ConnectionLog.Info("disconnected", new { connection = ConnectionName, reason = "manual", connectedSeconds = uptime });
          }
          else if (now.Ticks < Volatile.Read(ref healthHangUpUntilTicks)) {
            // Already announced as unresponsive; still an outage, so its end is reported.
            Volatile.Write(ref healthHangUpUntilTicks, 0);
            lostAt = now;
            ConnectionLog.Info("disconnected", new { connection = ConnectionName, reason = "health_check", connectedSeconds = uptime });
          }
          else {
            lostAt = now;
            ConnectionLog.Warning("connection_lost", new { connection = ConnectionName, connectedSeconds = uptime });
            bad = mSettingsManager.Reconnect ? "VPN connection lost. Reconnecting..." : "VPN connection lost.";
          }
        }
        // First sample at startup finding the VPN down: nothing happened, nothing to say.
      }
      if (bad != null)
        OnNotify?.Invoke(bad, true);
      if (good != null)
        OnNotify?.Invoke(good, false);
    }

    private static string Describe(TimeSpan span) {
      if (span.TotalMinutes < 1)
        return $"{(int)span.TotalSeconds} s";
      if (span.TotalHours < 1)
        return $"{(int)span.TotalMinutes} min";
      return $"{(int)span.TotalHours} h {span.Minutes} min";
    }

    #endregion

    #region tunnel health

    private DateTime nextHealthCheckAt = DateTime.MinValue;
    private int healthFailures;
    private bool healthReconnectTried;
    private bool healthGivenUp;
    private static readonly TimeSpan HealthHangUpWindow = TimeSpan.FromSeconds(30);
    private long healthHangUpUntilTicks; // a drop before this instant was caused by the check

    private string healthHost;
    private volatile bool healthResetRequested;

    /// <summary>
    /// Starts the check afresh, for the settings UI. After giving up on a host, the check
    /// stays quiet until that host answers; a new host or re-enabling it deserves a new try.
    /// </summary>
    public void ResetHealthCheck() => healthResetRequested = true;

    private void ResetHealthState() {
      healthFailures = 0;
      healthReconnectTried = healthGivenUp = false;
      nextHealthCheckAt = DateTime.MinValue;
    }

    private void CheckTunnelHealth() {
      var host = mSettingsManager.HealthCheckHost;
      // Consumed here, on the watchdog thread that owns the health fields.
      if (healthResetRequested || host != healthHost) {
        healthResetRequested = false;
        healthHost = host;
        ResetHealthState();
      }
      if (!mSettingsManager.HealthCheckEnabled || string.IsNullOrEmpty(host)) {
        ResetHealthState();
        return;
      }
      if (DateTime.Now < nextHealthCheckAt)
        return;
      nextHealthCheckAt = DateTime.Now + HealthCheckInterval;

      var result = PingHost(host);
      if (result.Success) {
        if (healthFailures > 0 || healthGivenUp)
          ConnectionLog.Info("health_check_recovered", new { connection = ConnectionName, host });
        healthFailures = 0;
        healthReconnectTried = healthGivenUp = false;
        return;
      }

      healthFailures++;
      if (healthGivenUp)
        return; // already reported; stay quiet until the host answers again
      ConnectionLog.Warning("health_check_failed", new { connection = ConnectionName, host, failures = healthFailures, error = result.Error });
      if (healthFailures < HealthCheckFailureLimit)
        return;
      healthFailures = 0;

      // Reconnecting did not help last time: the host itself is the likelier culprit, and
      // reconnecting every minute and a half forever would only cut the user off.
      if (healthReconnectTried) {
        healthGivenUp = true;
        ConnectionLog.Error("health_check_inconclusive", new { connection = ConnectionName, host });
        OnNotify?.Invoke($"{host} still does not answer after reconnecting. Check that it answers pings.", true);
        return;
      }

      var reconnect = mSettingsManager.Reconnect;
      ConnectionLog.Error("tunnel_unresponsive", new { connection = ConnectionName, host, action = reconnect ? "reconnect" : "none" });
      OnNotify?.Invoke(reconnect
        ? $"The VPN is up but {host} does not answer. Reconnecting..."
        : $"The VPN is up but {host} does not answer.", true);
      if (!reconnect) {
        // Said once. Without this, the same alert came back every 90 s for as long as the
        // host stayed silent; the next successful ping re-arms it.
        healthGivenUp = true;
        return;
      }
      healthReconnectTried = true;
      // The drop this hang-up causes is ours, not news. A time window rather than a flag: the
      // interface can take longer to go down than the hang-up waits, and a flag cleared too
      // early mislabelled that drop, while one left set mislabelled the next real one.
      Volatile.Write(ref healthHangUpUntilTicks, (DateTime.Now + HealthHangUpWindow).Ticks);
      if (DisconnectFromVpn() == null) {
        TrackState(); // usually sees the drop already, since the hang-up waited for it
        RestoreConnection(allowDialog: false);
      }
      else {
        Volatile.Write(ref healthHangUpUntilTicks, 0); // nothing was hung up
      }
    }

    /// <summary>One ping, for the watchdog and for the Test button.</summary>
    public static (bool Success, long RoundtripMs, string Error) PingHost(string host) {
      try {
        using (var ping = new Ping()) {
          var reply = ping.Send(host, HealthCheckTimeoutMs);
          if (reply.Status == IPStatus.Success)
            return (true, reply.RoundtripTime, null);
          return (false, 0, reply.Status == IPStatus.TimedOut ? "no reply" : reply.Status.ToString());
        }
      }
      catch (PingException ex) {
        // The useful part, "host not found" and the like, is in the inner exception.
        return (false, 0, ex.InnerException?.Message ?? ex.Message);
      }
      catch (Exception ex) {
        return (false, 0, ex.Message);
      }
    }

    #endregion
  }
}
