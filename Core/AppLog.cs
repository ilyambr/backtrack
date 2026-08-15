using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Backtrack;

/// <summary>
/// A small in-memory ring buffer of recent app-level events (OBS connect/
/// disconnect, clips saved, update checks/installs, RAM disk mount/unmount,
/// errors) -- backs the bottom-right overlay log's "Backtrack" mode. Nothing
/// here existed before; previously this kind of thing only ever went to
/// Debug.WriteLine, invisible outside an attached debugger. The ring buffer
/// itself is still deliberately not persisted -- FileLoggingEnabled below
/// (Settings > Experimental > Diagnostics > "Diagnostic log file") is the
/// opt-in for that, added after a real debugging session needed hand-added
/// temporary file logging repeatedly, precisely because nothing here
/// survived a restart to look back at afterward.
/// </summary>
public static class AppLog
{
    public sealed record Entry(DateTime TimestampLocal, string Message);

    private const int Capacity = 100;
    private static readonly LinkedList<Entry> _entries = new();
    private static readonly object _lock = new();

    /// <summary>Fires after every Write, on whatever thread called Write -- subscribers hop to the UI thread themselves.</summary>
    public static event Action? Changed;

    /// <summary>Set once at startup from AppSettings.DiagnosticLogEnabled, and again whenever that Settings toggle changes.</summary>
    public static bool FileLoggingEnabled { get; set; }

    /// <summary>Set once at startup from AppSettings.DeveloperModeEnabled, and again whenever that Settings toggle changes -- see WriteError.</summary>
    public static bool DeveloperModeEnabled { get; set; }

    public static string LogFilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backtrack", "logs", "backtrack.log");

    // Rotated (not appended past) at this size -- an always-on file log
    // across many sessions would otherwise grow forever. One rollover file
    // kept, not a numbered series: this is a recent-activity diagnostic aid,
    // not a long-term audit trail.
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

    /// <summary>
    /// context/ex.Message normally, or the full exception (stack trace and
    /// all) when DeveloperModeEnabled -- see its own comment on why that's
    /// gated separately from FileLoggingEnabled rather than always-verbose.
    /// Still goes through the same Write above either way, so it shows in
    /// the overlay log too, not just the file.
    /// </summary>
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
            // Best effort -- a locked/missing/permission-denied log file is
            // never worth surfacing an error over; the in-memory ring buffer
            // above already has this entry regardless.
        }
    }

    /// <summary>Oldest first, matching how the log panel displays them (newest at the bottom, like a terminal).</summary>
    public static List<Entry> Snapshot()
    {
        lock (_lock)
            return _entries.ToList();
    }
}
