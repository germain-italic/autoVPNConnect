using sergiye.Common;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AutoVPNConnect {

  public partial class MainForm : Form {

    private readonly PersistentSettings settings;
    private readonly SettingsManager mSettingsManager;
    private readonly ConnectionManager mConnectionManager;
    private readonly NotifyIconAdv mNotifyIcon;
    private readonly ToolStripMenuItem menuItemAutoStart;
    private readonly ToolStripMenuItem menuItemReconnect;
    private readonly ToolStripMenuItem menuItemConnect;
    private readonly MenuItem themeMenuItem;
    private readonly Icon greenIcon;
    private readonly Icon redIcon;
    private readonly Icon yellowIcon;
    private readonly ToolTip statusToolTip = new ToolTip();
    private bool showApp;
    private bool loadingSettings;
    private bool recoveryTaskChecked;
    private string reportedError;

    #region form decoration

    private const int SysMenuAboutId = 0x1;
    private const int SysMenuTopMost = 0x2;
    private const int SysMenuAppSite = 0x3;
    private const int SysMenuCheckUpdates = 0x4;

    protected override void OnHandleCreated(EventArgs e) {

      base.OnHandleCreated(e);

      var hSysMenu = WinApiHelper.GetSystemMenu(Handle, false);

      WinApiHelper.DeleteMenu(hSysMenu, WinApiHelper.SC_SIZE, WinApiHelper.MF_BY_COMMAND);
      // WinApiHelper.DeleteMenu(hSysMenu, WinApiHelper.SC_MINIMIZE, WinApiHelper.MF_BY_COMMAND);
      WinApiHelper.DeleteMenu(hSysMenu, WinApiHelper.SC_MAXIMIZE, WinApiHelper.MF_BY_COMMAND);

      uint menuIndex = 1;
      WinApiHelper.InsertMenu(hSysMenu, ++menuIndex, WinApiHelper.MF_BY_POSITION | WinApiHelper.MF_SEPARATOR, 0, string.Empty);
      var checkedAttr = TopMost ? WinApiHelper.MF_CHECKED : WinApiHelper.MF_UNCHECKED;
      WinApiHelper.InsertMenu(hSysMenu, ++menuIndex, WinApiHelper.MF_BY_POSITION | checkedAttr, SysMenuTopMost, "Always on top");
      WinApiHelper.InsertMenu(hSysMenu, ++menuIndex, WinApiHelper.MF_BY_POSITION | WinApiHelper.MF_POPUP, (int)themeMenuItem.Handle, themeMenuItem.Text);
      themeMenuItem.Tag = menuIndex;
      WinApiHelper.InsertMenu(hSysMenu, ++menuIndex, WinApiHelper.MF_BY_POSITION, SysMenuAppSite, "Site");
      WinApiHelper.InsertMenu(hSysMenu, ++menuIndex, WinApiHelper.MF_BY_POSITION, SysMenuCheckUpdates, "Check for updates");
      WinApiHelper.InsertMenu(hSysMenu, ++menuIndex, WinApiHelper.MF_BY_POSITION, SysMenuAboutId, "About…");
    }

    protected override void WndProc(ref Message m) {

      base.WndProc(ref m);
      if (m.Msg == WinApiHelper.WM_SYS_COMMAND) {
        switch ((int)m.WParam) {
          case SysMenuAboutId:
            Updater.ShowAbout();
            break;
          case SysMenuCheckUpdates:
            Updater.CheckForUpdates(Updater.CheckUpdatesMode.AllMessages);
            break;
          case SysMenuAppSite:
            Updater.VisitAppSite();
            break;
          case SysMenuTopMost:
            ToggleTopMost();
            break;
        }
      }
      else if (m.Msg == WinApiHelper.WM_SHOWME) {
        SetAppVisible(true);
      }
    }

    private void ToggleTopMost() {
      TopMost = !TopMost;
      settings.SetValue("AlwaysOnTop", TopMost);
      var hSysMenu = WinApiHelper.GetSystemMenu(Handle, false);
      WinApiHelper.CheckMenuItem(hSysMenu, SysMenuTopMost, TopMost ? WinApiHelper.MF_CHECKED : WinApiHelper.MF_UNCHECKED);
    }

    private static bool DoSnap(int pos, int edge) {
      return Math.Abs(pos - edge) <= 50;
    }

    private void MainForm_ResizeEnd(object sender, EventArgs e) {
      var scn = Screen.FromPoint(Location);
      if (DoSnap(Left, scn.WorkingArea.Left)) Left = scn.WorkingArea.Left;
      if (DoSnap(Top, scn.WorkingArea.Top)) Top = scn.WorkingArea.Top;
      if (DoSnap(scn.WorkingArea.Right, Right)) Left = scn.WorkingArea.Right - Width;
      if (DoSnap(scn.WorkingArea.Bottom, Bottom)) Top = scn.WorkingArea.Bottom - Height;
    }

    #endregion form decoration

    public MainForm() {
      InitializeComponent();

      Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
      greenIcon = Icon;
      Text = Updater.ApplicationTitle;
      themeMenuItem = new MenuItem("&Themes");

      // TaskbarProgressHelper.Handle = this.Handle;

      settings = new PersistentSettings();
      settings.Load();
      mSettingsManager = new SettingsManager(settings);
      // Load editable fields once: status notifications must not overwrite unsaved input.
      if (mSettingsManager.IsConnectionConfigured) {
        cmbConnections.Items.Add(mSettingsManager.VpnConnectionName);
        cmbConnections.SelectedIndex = 0;
      }
      textBoxUsername.Text = mSettingsManager.UserName;
      mConnectionManager = new ConnectionManager(ref mSettingsManager);
      mConnectionManager.OnStatusChanged += UpdateUI;

      var assembly = Assembly.GetExecutingAssembly();
      using (var st = assembly.GetManifestResourceStream("AutoVPNConnect.Resources.Red.ico")) {
        if (st != null) redIcon = new Icon(st);
      }
      using (var st = assembly.GetManifestResourceStream("AutoVPNConnect.Resources.Yellow.ico")) {
        if (st != null) yellowIcon = new Icon(st);
      }

      TopMost = settings.GetValue("AlwaysOnTop", false);
      if (mSettingsManager.IsConnectionConfigured == false) {
        lblConnectionStatus.Text = "Connection status: Disconnected";
      }

      menuItemAutoStart = new ToolStripMenuItem(cbxAutoStart.Text, null, (_, _) => { cbxAutoStart.Checked = !cbxAutoStart.Checked; });
      menuItemReconnect = new ToolStripMenuItem("Restore connection", null, menuReconnect_Click);
      menuItemConnect = new ToolStripMenuItem("Connect", null, btnToggle_Click);
      btnToggle.Click += btnToggle_Click;
      mNotifyIcon = new NotifyIconAdv {
        ContextMenuStrip = new ContextMenuStrip(),
        Icon = yellowIcon ?? Icon,
      };
      mNotifyIcon.ContextMenuStrip.Items.AddRange(
        new ToolStripItem[] {
          new ToolStripMenuItem("Show/Hide", null, menuItemShowHide_Click),
          menuItemConnect,
          new ToolStripSeparator(),
          menuItemAutoStart,
          menuItemReconnect,
          new ToolStripSeparator(),
          new ToolStripMenuItem("Site", null, (_, _) => Updater.VisitAppSite()),
          new ToolStripMenuItem("Check for updates", null, (_, _) => Updater.CheckForUpdates(Updater.CheckUpdatesMode.AllMessages)),
          new ToolStripMenuItem("About…", null, (_, _) => Updater.ShowAbout()),
          new ToolStripSeparator(),
          new ToolStripMenuItem("Exit", null, toolStripMenuItemExit_Click)
        }
      );

      mNotifyIcon.MouseDoubleClick += menuItemShowHide_Click;
      loadingSettings = true;
      cbxAutoStart.Checked = mSettingsManager.AutoStartApp;
      cbxReconnect.Checked = mSettingsManager.Reconnect;
      cbxFixStuckPort.Checked = mSettingsManager.FixStuckPort;
      loadingSettings = false;

      var runInBackground = new UserOption("StartApplicationMinimized", false, cbxRunInBackground, settings);
      runInBackground.Changed += (_, _) => {
        mNotifyIcon.Visible = cbxRunInBackground.Checked;
      };
      showApp = !runInBackground.Value;
      mNotifyIcon.Visible = runInBackground.Value;

      ResizeEnd += MainForm_ResizeEnd;
      
      Updater.Subscribe(
        (message, isError) => {
          if (InvokeRequired)
            Invoke(new Action(() => MessageBox.Show(message, Updater.ApplicationName, MessageBoxButtons.OK, isError ? MessageBoxIcon.Warning : MessageBoxIcon.Information)));
          else
            MessageBox.Show(message, Updater.ApplicationName, MessageBoxButtons.OK, isError ? MessageBoxIcon.Warning : MessageBoxIcon.Information);
        },
        message => InvokeRequired
          ? (bool)Invoke(new Func<bool>(() => MessageBox.Show(this, message, Updater.ApplicationName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK))
          : MessageBox.Show(this, message, Updater.ApplicationName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK,
        () => toolStripMenuItemExit_Click(null, EventArgs.Empty)
      );
      
      InitializeTheme();

      //if (mSettingsManager.AutoStartApp && !mSettingsManager.Reconnect && !showApp) {
      //  //auto-connect on first start
      //  Task.Run(() => {
      //    mConnectionManager?.RestoreConnection();
      //  });
      //}
    }

    #region themes

    private void OnThemeCurrentChanged() {
      Visible = false;
      settings.SetValue("Theme", Theme.IsAutoThemeEnabled ? "auto" : Theme.Current.Id);
      UpdateUI();
      Visible = showApp;
    }

    private void InitializeTheme() {

      mNotifyIcon.ContextMenuStrip.Renderer = new ThemedToolStripRenderer();

      var menuHandle = WinApiHelper.GetSystemMenu(Handle, false);
      WinApiHelper.RemoveMenu(menuHandle, (uint)themeMenuItem.Tag, WinApiHelper.MF_BY_POSITION);
      themeMenuItem.MenuItems.Clear();

      var currentItem = CustomTheme.FillThemesMenu((text, theme, onClick) => {
        var item = new RadioButtonMenuItem(text, onClick);
        themeMenuItem.MenuItems.Add(item);
        item.Tag = theme;
        return item;
      }, OnThemeCurrentChanged, settings.GetValue("Theme", ""), Updater.ApplicationName + ".themes");
      WinApiHelper.InsertMenu(menuHandle, (uint)themeMenuItem.Tag, WinApiHelper.MF_BY_POSITION | WinApiHelper.MF_POPUP, (int)themeMenuItem.Handle, themeMenuItem.Text);
      currentItem?.PerformClick();
      Theme.Current.Apply(this);
    }

    #endregion

    private void btnToggle_Click(object sender, EventArgs e) {
      btnToggle.Enabled = menuItemConnect.Enabled = false;
      mConnectionManager.ToggleConnection();
    }

    protected override void SetVisibleCore(bool value) {
      base.SetVisibleCore(showApp ? value : showApp);
    }
    
    private void comboBoxActiveVPNConnections_DropDown(object sender, EventArgs e) {
      cmbConnections.Items.Clear();
      var vpnConnections = ConnectionManager.GetActiveVpnConnections();
      foreach (var @interface in vpnConnections) {
        cmbConnections.Items.Add(@interface.Name);
      }
      if (cmbConnections.Items.Count == 0) {
        if (MessageBox.Show("Connect to a VPN first?", Updater.ApplicationTitle, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK) {
          Process.Start(new ProcessStartInfo("NCPA.cpl") { UseShellExecute = true });
        }
        else {
          var connectionName = mSettingsManager.VpnConnectionName;
          if (!connectionName.IsNullOrEmpty())
            cmbConnections.Items.Add(connectionName);
        }
      }
    }

    private void btnSaveSettings_Click(object sender, EventArgs e) {
      var vpnConnectionName = cmbConnections.Text;
      var userName = textBoxUsername.Text;
      var password = textBoxPassword.Text;

      if (vpnConnectionName.IsNullOrEmpty()) {
        MessageBox.Show("Connection is not configured", Updater.ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
      else {
        mSettingsManager.VpnConnectionName = vpnConnectionName;
        mSettingsManager.UserName = userName;
        mSettingsManager.Password = password;

        MessageBox.Show("Settings successfully saved.\n" +
        $"{Updater.ApplicationTitle} will automatically connect to: " +
        vpnConnectionName + "\nIf this does not work, enter your username and password again.",
          Updater.ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Information);
      }
    }

    private void cbxAutoStart_CheckedChanged(object sender, EventArgs e) {
      menuItemAutoStart.Checked = mSettingsManager.AutoStartApp = cbxAutoStart.Checked;
    }

    protected override void OnShown(EventArgs e) {
      base.OnShown(e);
      // The setting defaults to on, so a fresh install would otherwise never register the
      // task and the first 633 would raise UAC from a background thread. Do it the first time
      // the user actually looks at the window - never at boot, where the app starts minimised
      // and the prompt would go unnoticed.
      if (!recoveryTaskChecked && mSettingsManager.FixStuckPort)
        TryRegisterRecoveryTask();
    }

    private void cbxFixStuckPort_CheckedChanged(object sender, EventArgs e) {
      mSettingsManager.FixStuckPort = cbxFixStuckPort.Checked;
      if (loadingSettings)
        return;

      if (cbxFixStuckPort.Checked) {
        TryRegisterRecoveryTask();
        return;
      }

      if (RasManService.IsElevated || !RasManService.IsTaskRegistered())
        return;
      if (MessageBox.Show("Also remove the scheduled task that was created to restart the RasMan service?",
            Updater.ApplicationTitle, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        return;
      if (!RasManService.RemoveTask(out var removeError))
        MessageBox.Show("The scheduled task could not be removed.\n\n" + removeError,
          Updater.ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Registers the elevated helper while the user is in front of the application. Doing it
    /// here rather than at the first failure is the whole point: a UAC prompt raised from the
    /// background thread of a failing reconnect, with the window hidden, would be missed.
    /// </summary>
    private void TryRegisterRecoveryTask() {
      recoveryTaskChecked = true;
      if (RasManService.IsElevated)
        return;

      // An upgrade replaces the task, which costs one more UAC prompt - but only on the first
      // window opening after the action itself changed, and it is the only moment the user is
      // there to answer it.
      var registered = RasManService.IsTaskRegistered();
      var outdated = registered && RasManService.IsTaskOutdated();
      if (registered && !outdated)
        return;

      if (RasManService.RegisterTask(out var error))
        return;

      // The elevated schtasks can still have succeeded after we stopped waiting on it, so
      // check before telling the user it failed and turning the feature off under them.
      if (RasManService.IsTaskRegistered() && !RasManService.IsTaskOutdated())
        return;

      // A task from an older build still restarts the service; it just handles a restart that
      // fails less well. Keeping it beats nagging the user and turning the feature off.
      if (outdated)
        return;

      MessageBox.Show("Restarting the RasMan service could not be set up, so error 633 will " +
        "have to be fixed by hand.\n\n" + error, Updater.ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      loadingSettings = true;
      cbxFixStuckPort.Checked = false;
      loadingSettings = false;
      mSettingsManager.FixStuckPort = false;
    }

    private void cbxReconnect_CheckedChanged(object sender, EventArgs e) {
      mSettingsManager.Reconnect = cbxReconnect.Checked;
      menuItemReconnect.Checked = cbxReconnect.Checked;
      if (cbxReconnect.Checked) {
        Task.Run(() => {
          mConnectionManager?.RestoreConnection();
        });
      }
    }

    private void UpdateUI() {

      if (InvokeRequired) {
        Invoke(new Action(UpdateUI));
        return;
      }

      var connectionName = mSettingsManager?.VpnConnectionName;
      var isConnecting = mConnectionManager?.IsBusy ?? false;
      var isConnected = mConnectionManager?.VpnIsConnected() ?? false;
      var isConnectedText = isConnecting ? "Busy" : isConnected ? "Connected" : "Disconnected";
      btnToggle.Text = menuItemConnect.Text = isConnected ? "Disconnect" : "Connect";
      btnToggle.Enabled = menuItemConnect.Enabled = !connectionName.IsNullOrEmpty() && !isConnecting;

      lblConnectionStatus.Text = "Connection status: " + isConnectedText;
      lblConnectionStatus.ForeColor = isConnecting ? Theme.Current.InfoColor : isConnected ? Theme.Current.MessageColor : Theme.Current.WarnColor;
      if (isConnected)
        reportedError = null; // a success re-arms the announcement for the next failure
      // Hidden while connecting (the attempt clears it anyway) and once connected, including
      // when the connection came up outside the application and LastError is simply stale.
      ShowLastError(isConnected || isConnecting ? null : mConnectionManager?.LastError);

      Icon = mNotifyIcon.Icon = isConnecting ? yellowIcon : isConnected ? greenIcon : redIcon;
      // TaskbarProgressHelper.SetOverlay(Icon.Handle, this.Handle, "test");
      mNotifyIcon.Text = Updater.ApplicationTitle;
      if (!connectionName.IsNullOrEmpty()) {
        mNotifyIcon.Text += $"\n{connectionName} - {isConnectedText}";
      }
      //mNotifyIcon.BalloonTipTitle = Updater.ApplicationTitle;
      //mNotifyIcon.BalloonTipText = $"{Updater.ApplicationTitle} runs in background";
      //mNotifyIcon.ShowBalloonTip(1000);

    }

    /// <summary>
    /// Surfaces why a connection attempt failed, in its own fixed-size label so that a long
    /// message cannot grow over the buttons. The full text goes to the tooltip, and a balloon
    /// announces it once - the application usually sits minimised when a reconnect fails.
    /// </summary>
    private void ShowLastError(string error) {

      if (string.IsNullOrEmpty(error)) {
        // reportedError is deliberately left alone here: it is cleared only once a connection
        // succeeds, so one failure repeating across a burst of network events is announced once.
        lblLastError.Text = string.Empty;
        statusToolTip.SetToolTip(lblLastError, string.Empty);
        return;
      }

      lblLastError.Text = error;
      lblLastError.ForeColor = Theme.Current.WarnColor;
      statusToolTip.SetToolTip(lblLastError, error);

      if (error == reportedError)
        return; // already announced, do not nag on every network change
      reportedError = error;
      mNotifyIcon.BalloonTipTitle = Updater.ApplicationTitle;
      mNotifyIcon.BalloonTipText = error;
      mNotifyIcon.ShowBalloonTip(5000);
    }

    private void menuReconnect_Click(object sender, EventArgs e) {
      cbxReconnect.Checked = !cbxReconnect.Checked;
    }

    private void menuItemShowHide_Click(object sender, EventArgs e) {
      SetAppVisible(!showApp);
    }

    private void SetAppVisible(bool visible) {
      Visible = showApp = visible;
      if (Visible) {
        if (WindowState == FormWindowState.Minimized)
          WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
        //bool top = TopMost;
        //TopMost = true;
        //TopMost = top;
      }
    }

    private void toolStripMenuItemExit_Click(object sender, EventArgs e) {
      mNotifyIcon.Visible = false;
      Environment.Exit(0);
    }

    private void MainForm_Closing(object sender, FormClosingEventArgs e) {
      if (cbxRunInBackground.Checked) {
        showApp = false;
        Visible = false;
        e.Cancel = true;
      }
      // else {
      //   TaskbarProgressHelper.ClearOverlay();
      // }
    }
  }
}
