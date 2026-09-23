using sergiye.Common;
using System;
using System.Net;
using System.Runtime.CompilerServices;
// using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AutoVPNConnect {
  static class Program {

    // [DllImport("shell32.dll")]
    // public static extern int SetCurrentProcessExplicitAppUserModelID([MarshalAs(UnmanagedType.LPWStr)] string AppID);

    [STAThread]
    static void Main() {

      Crasher.Listen();
      // SetCurrentProcessExplicitAppUserModelID(Updater.ApplicationCompany + "." + Updater.ApplicationName);

      // Updater's static constructor installs a process-wide certificate callback that accepts
      // any certificate, and the updater downloads and runs a replacement executable. Run that
      // constructor now, then restore normal TLS validation before any request is made.
      RuntimeHelpers.RunClassConstructor(typeof(Updater).TypeHandle);
      ServicePointManager.ServerCertificateValidationCallback = null;

      if (WinApiHelper.CheckRunningInstances(true, true)) {
        MessageBox.Show($"{Updater.ApplicationTitle} is already running.\nIt is recommended to close this instance.", Updater.ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        Environment.Exit(0);
      }

      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      Application.Run(new MainForm());
    }
  }
}
