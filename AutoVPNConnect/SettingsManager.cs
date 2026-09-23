using sergiye.Common;
using System;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace AutoVPNConnect {
  class SettingsManager {
    private readonly PersistentSettings settings;

    // Passwords used to be 3DES-encrypted with this key, hardcoded in public source: anyone
    // could decrypt them. Kept only to read such a value once and store it again with DPAPI.
    private static readonly string legacyEncryptionKey = "123456789012345678901234";
    private const string ProtectedPrefix = "dpapi:";
    private static readonly byte[] ProtectionEntropy = Encoding.UTF8.GetBytes("AutoVPNConnect.Password");

    public SettingsManager(PersistentSettings settings) {
      this.settings = settings;
    }

    internal bool IsConnectionConfigured => !string.IsNullOrEmpty(VpnConnectionName) && VpnConnectionName != "No settings found";

    // DPAPI ties the ciphertext to the Windows user account: another account, or a copy of the
    // settings taken to another machine, cannot decrypt it.
    private static string Protect(string value) {
      if (string.IsNullOrEmpty(value))
        return null;
      var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), ProtectionEntropy, DataProtectionScope.CurrentUser);
      return ProtectedPrefix + Convert.ToBase64String(bytes);
    }

    private static string Unprotect(string stored) {
      var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored.Substring(ProtectedPrefix.Length)), ProtectionEntropy, DataProtectionScope.CurrentUser);
      return Encoding.UTF8.GetString(bytes);
    }

    private static string DecryptLegacy(string encryptedPassword) {
      if (string.IsNullOrEmpty(encryptedPassword))
        return encryptedPassword;

      using (var tripleDes = new TripleDESCryptoServiceProvider {
        Key = Encoding.UTF8.GetBytes(legacyEncryptionKey),
        Mode = CipherMode.ECB,
        Padding = PaddingMode.PKCS7
      }) {
        using (var cTransform = tripleDes.CreateDecryptor()) {
          var inputArray = Convert.FromBase64String(encryptedPassword);
          var resultArray = cTransform.TransformFinalBlock(inputArray, 0, inputArray.Length);
          tripleDes.Clear();
          return Encoding.UTF8.GetString(resultArray);
        }
      }
    }

    private bool GetApplicationStartWithSystem() {
      try {
        using (var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run")) {
          if (key != null) {
            // Quoted since this fork; entries written by earlier versions are not.
            var regValue = key.GetValue(Updater.ApplicationName)?.ToString()?.Trim('"');
            return Updater.CurrentFileLocation.Equals(regValue, StringComparison.OrdinalIgnoreCase);
          }
        }
      }
      catch {
        //ignore
      }
      return false;
    }

    private void SetApplicationStartWithSystem(bool enabled) {
      try {
        using (var key = Registry.CurrentUser.OpenSubKey("SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Run", true)) {
          if (key != null) {
            if (enabled)
              // Unquoted, a path with spaces lets Windows try "C:\Program.exe" and the like first.
              key.SetValue(Updater.ApplicationName, '"' + Application.ExecutablePath + '"');
            else
              key.DeleteValue(Updater.ApplicationName, false);
          }
        }
      }
      catch (Exception ex) {
        MessageBox.Show("Error while changing application auto start: " + ex.Message, Updater.ApplicationTitle, MessageBoxButtons.OK, MessageBoxIcon.Warning);
      }
    }

    public string VpnConnectionName {
      get => settings.GetValue("VPNConnectionName", "No settings found");
      set => settings.SetValue("VPNConnectionName", value);
    }

    public string UserName {
      get => settings.GetValue("Username", "");
      set => settings.SetValue("Username", value ?? "");
    }

    public string Password {
      get {
        var stored = settings.GetValue("Password", "");
        if (string.IsNullOrEmpty(stored))
          return stored;
        try {
          if (stored.StartsWith(ProtectedPrefix, StringComparison.Ordinal))
            return Unprotect(stored);
          // Written by an earlier version: re-store it under DPAPI, so the weak form does
          // not stay on disk any longer than the first read.
          var password = DecryptLegacy(stored);
          try {
            Password = password;
          }
          catch (Exception) {
            // Re-storing failed; the password was still read, so use it and retry next time.
          }
          return password;
        }
        catch (Exception) {
          // Unreadable, e.g. settings copied from another Windows account. Without a password
          // the dial falls back to the credentials Windows holds, or to the dialog.
          return "";
        }
      }
      set => settings.SetValue("Password", Protect(value) ?? "");
    }

    public bool AutoStartApp {
      get => GetApplicationStartWithSystem();
      set => SetApplicationStartWithSystem(value);
    }

    public bool Reconnect {
      get => settings.GetValue("ApplicationEnabled", false);
      set => settings.SetValue("ApplicationEnabled", value);
    }

    /// <summary>
    /// Allows restarting the RasMan service to recover from RAS error 633, where Windows
    /// keeps a VPN port marked as open and every later dial fails until the service is
    /// restarted. On by default: without it such a failure needs manual intervention.
    /// </summary>
    public bool FixStuckPort {
      get => settings.GetValue("FixStuckPort", true);
      set => settings.SetValue("FixStuckPort", value);
    }
  }
}
