using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace AutoVPNConnect {

  /// <summary>
  /// Connection history in JSON Lines (https://jsonlines.org): one JSON object per line, with
  /// an RFC 3339 local timestamp, a level and an event name. Every line parses on its own, so
  /// jq, PowerShell's ConvertFrom-Json or a log collector read it without a custom parser.
  /// Logging must never take the application down: every failure here is swallowed.
  /// </summary>
  static class ConnectionLog {

    private const long MaxBytes = 1024 * 1024; // then rotated to a single .1 backup

    private static readonly object writeLock = new object();
    private static readonly JavaScriptSerializer serializer = new JavaScriptSerializer();

    public static string FilePath { get; } = Path.Combine(
      Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
      "AutoVPNConnect", "connection.jsonl");

    public static void Info(string eventName, object fields = null) => Write("info", eventName, fields);
    public static void Warning(string eventName, object fields = null) => Write("warning", eventName, fields);
    public static void Error(string eventName, object fields = null) => Write("error", eventName, fields);

    private static void Write(string level, string eventName, object fields) {
      try {
        // Fixed keys first, so every line reads the same way.
        var entry = new Dictionary<string, object> {
          ["time"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture),
          ["level"] = level,
          ["event"] = eventName,
        };
        if (fields != null) {
          foreach (var property in fields.GetType().GetProperties()) {
            var value = property.GetValue(fields);
            if (value != null)
              entry[property.Name] = value;
          }
        }
        var line = serializer.Serialize(entry) + "\n";

        lock (writeLock) {
          Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
          var file = new FileInfo(FilePath);
          if (file.Exists && file.Length > MaxBytes) {
            var backup = Path.ChangeExtension(FilePath, ".1.jsonl");
            File.Delete(backup);
            File.Move(FilePath, backup);
          }
          File.AppendAllText(FilePath, line, new UTF8Encoding(false));
        }
      }
      catch {
        // A full disk or a locked file must not break reconnecting.
      }
    }
  }
}
