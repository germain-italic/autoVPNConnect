namespace AutoVPNConnect {
  // Same surface as the application's ConnectionLog, writing nothing: the probe must not add
  // entries to the user's real connection history.
  static class ConnectionLog {
    public static void Info(string eventName, object fields = null) { }
    public static void Warning(string eventName, object fields = null) { }
    public static void Error(string eventName, object fields = null) { }
  }
}
