using System;
using System.Collections.Generic;
using System.Linq;

namespace Backtrack;

/// <summary>
/// A small in-memory ring buffer of recent app-level events (OBS connect/
/// disconnect, clips saved, update checks/installs, RAM disk mount/unmount,
/// errors) -- backs the bottom-right overlay log's "Backtrack" mode. Nothing
/// here existed before; previously this kind of thing only ever went to
/// Debug.WriteLine, invisible outside an attached debugger. Deliberately not
/// persisted to disk -- this is a "what's happened recently" glance, not a
/// diagnostic log file.
/// </summary>
public static class AppLog
{
    public sealed record Entry(DateTime TimestampLocal, string Message);

    private const int Capacity = 100;
    private static readonly LinkedList<Entry> _entries = new();
    private static readonly object _lock = new();

    /// <summary>Fires after every Write, on whatever thread called Write -- subscribers hop to the UI thread themselves.</summary>
    public static event Action? Changed;

    public static void Write(string message)
    {
        lock (_lock)
        {
            _entries.AddLast(new Entry(DateTime.Now, message));
            if (_entries.Count > Capacity)
                _entries.RemoveFirst();
        }
        Changed?.Invoke();
    }

    /// <summary>Oldest first, matching how the log panel displays them (newest at the bottom, like a terminal).</summary>
    public static List<Entry> Snapshot()
    {
        lock (_lock)
            return _entries.ToList();
    }
}
