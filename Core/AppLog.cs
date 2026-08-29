using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backtrack;

public static class AppLog
{
    public sealed record Entry(DateTime TimestampLocal, string Message);

    private const int Capacity = 100;
    private static readonly LinkedList<Entry> _entries = new();
    private static readonly object _lock = new();

    public static event Action? Changed;

    public static bool FileLoggingEnabled { get; set; }

    public static bool DeveloperModeEnabled { get; set; }

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backtrack", "logs", "backtrack.log");

    private const long MaxFileBytes = 5 * 1024 * 1024;

    public static void Write(string message)
    {
        lock (_lock)
        {
            _entries.AddLast(new Entry(DateTime.Now, message));
            if (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
        Changed?.Invoke();

        if (FileLoggingEnabled)
            WriteToFile(message);
    }

    public static void WriteError(string context, Exception ex) =>
        Write(DeveloperModeEnabled ? $"{context}: {ex}" : $"{context}: {ex.Message}");

    private static void WriteToFile(string message)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogFilePath);
            if (dir is not null)
                Directory.CreateDirectory(dir);

            if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > MaxFileBytes)
            {
                string rolledPath = LogFilePath + ".old";
                try { File.Delete(rolledPath); } catch { /* best effort */ }
                File.Move(LogFilePath, rolledPath);
            }

            File.AppendAllLines(LogFilePath, new[] { $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}" });
        }
        catch
        {
        }
    }

    public static List<Entry> Snapshot()
    {
        lock (_lock)
            return _entries.ToList();
    }
}
