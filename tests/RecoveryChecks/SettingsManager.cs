namespace AutoVPNConnect {
  // Only the shape needed to compile ConnectionManager. No settings or credentials are read.
  class SettingsManager {
    public bool Reconnect { get; set; }
    public bool IsConnectionConfigured { get; set; }
    public bool FixStuckPort { get; set; }
    public string VpnConnectionName { get; set; }
    public string UserName { get; set; }
    public string Password { get; set; }
  }
}
