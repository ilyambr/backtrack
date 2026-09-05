using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Backtrack.Obs;

namespace Backtrack.Core;

public sealed record DeduplicationEntry(
    string ClipFileName,
    string ClipPath,
    string OriginClipFileName,
    string OriginClipPath,
    string SourceKey,
    int DurationSeconds,
    DateTime SavedAtUtc,
    double ExactDurationSeconds = 0.0);

public sealed class DeduplicationService
{
    private static readonly string DeduplicatedFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Backtrack", "deduplicated.json");

    private readonly ConcurrentDictionary<string, DateTime> _lastSaveUtc = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _lastSavedClipPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _pendingDeduplicatedSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, double> _pendingElapsedSeconds = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _pendingOriginClipPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _lock = new();
    private Dictionary<string, DeduplicationEntry> _records = new(StringComparer.OrdinalIgnoreCase);
    private int _lastObservedPreferredSeconds = -1;

    public static DeduplicationService Instance { get; } = new();

    public DeduplicationService()
    {
        Load();
    }

    public void OnClipDurationChanged(int newDurationSeconds)
    {
        if (_lastObservedPreferredSeconds > 0 && _lastObservedPreferredSeconds != newDurationSeconds)
        {
            ClearSaveTimestamps();
        }
        _lastObservedPreferredSeconds = newDurationSeconds;
    }

    public void ClearSaveTimestamps()
    {
        _lastSaveUtc.Clear();
        _lastSavedClipPath.Clear();
        _pendingDeduplicatedSeconds.Clear();
        _pendingElapsedSeconds.Clear();
        _pendingOriginClipPath.Clear();
    }

    public async Task<int?> PrepareReplaySaveAsync(ObsService obs, string rowKey, string? rowLabel, int preferredSeconds)
    {
        if (_lastObservedPreferredSeconds > 0 && _lastObservedPreferredSeconds != preferredSeconds)
        {
            ClearSaveTimestamps();
        }
        _lastObservedPreferredSeconds = preferredSeconds;

        int? deduplicatedSeconds = null;

        if (_lastSaveUtc.TryGetValue(rowKey, out DateTime lastSave))
        {
            double elapsed = (DateTime.UtcNow - lastSave).TotalSeconds;
            if (elapsed > 1 && elapsed < preferredSeconds)
            {
                int effectiveSeconds = (int)Math.Ceiling(elapsed);
                deduplicatedSeconds = effectiveSeconds;
                _pendingDeduplicatedSeconds[rowKey] = effectiveSeconds;
                _pendingElapsedSeconds[rowKey] = elapsed;

                if (_lastSavedClipPath.TryGetValue(rowKey, out string? originPath))
                {
                    _pendingOriginClipPath[rowKey] = originPath;
                }

                string originName = !string.IsNullOrEmpty(originPath) ? Path.GetFileName(originPath) : "unknown";
                AppLog.Write($"[Replay] Smart deduplication for {rowLabel ?? rowKey}: clipping {effectiveSeconds}s since last save (origin: {originName})");
                try { await obs.SetReplayRowLengthAsync(rowKey, effectiveSeconds); } catch { }
            }
            else
            {
                _pendingDeduplicatedSeconds.TryRemove(rowKey, out _);
                _pendingElapsedSeconds.TryRemove(rowKey, out _);
                _pendingOriginClipPath.TryRemove(rowKey, out _);
                if (preferredSeconds > 0)
                {
                    try { await obs.SetReplayRowLengthAsync(rowKey, preferredSeconds); } catch { }
                }
            }
        }
        else
        {
            _pendingDeduplicatedSeconds.TryRemove(rowKey, out _);
            _pendingElapsedSeconds.TryRemove(rowKey, out _);
            _pendingOriginClipPath.TryRemove(rowKey, out _);
            if (preferredSeconds > 0)
            {
                try { await obs.SetReplayRowLengthAsync(rowKey, preferredSeconds); } catch { }
            }
        }

        _lastSaveUtc[rowKey] = DateTime.UtcNow;
        return deduplicatedSeconds;
    }

    public DeduplicationEntry? RegisterSavedClip(string rowKey, string savedClipPath)
    {
        if (!_lastSaveUtc.ContainsKey(rowKey))
        {
            _lastSaveUtc[rowKey] = DateTime.UtcNow;
        }
        bool isDedup = _pendingDeduplicatedSeconds.TryRemove(rowKey, out int dedupSec);
        _pendingElapsedSeconds.TryRemove(rowKey, out double exactElapsed);
        _pendingOriginClipPath.TryRemove(rowKey, out string? originPath);

        string currentClipFileName = Path.GetFileName(savedClipPath);
        DeduplicationEntry? result = null;

        if (isDedup && !string.IsNullOrEmpty(savedClipPath))
        {
            string originFileName = !string.IsNullOrEmpty(originPath) ? Path.GetFileName(originPath) : "";
            result = new DeduplicationEntry(
                ClipFileName: currentClipFileName,
                ClipPath: savedClipPath,
                OriginClipFileName: originFileName,
                OriginClipPath: originPath ?? "",
                SourceKey: rowKey,
                DurationSeconds: dedupSec,
                SavedAtUtc: DateTime.UtcNow,
                ExactDurationSeconds: exactElapsed > 0 ? exactElapsed : dedupSec);

            lock (_lock)
            {
                _records[currentClipFileName] = result;
                _records[savedClipPath] = result;
            }
            Save();
        }

        if (!string.IsNullOrEmpty(savedClipPath))
        {
            _lastSavedClipPath[rowKey] = savedClipPath;
        }

        return result;
    }

    public bool IsDeduplicated(string clipPathOrName, out DeduplicationEntry? entry)
    {
        lock (_lock)
        {
            return _records.TryGetValue(clipPathOrName, out entry) ||
                   _records.TryGetValue(Path.GetFileName(clipPathOrName), out entry);
        }
    }

    public void RemoveRecord(string clipPathOrName)
    {
        lock (_lock)
        {
            string fileName = Path.GetFileName(clipPathOrName);
            bool removed = _records.Remove(clipPathOrName);
            if (!string.IsNullOrEmpty(fileName))
            {
                removed |= _records.Remove(fileName);
            }

            if (removed)
            {
                Save();
            }
        }
    }

    public void UpdateOriginAfterMerge(string oldOriginPathOrName, string newOriginPath)
    {
        lock (_lock)
        {
            string oldFileName = Path.GetFileName(oldOriginPathOrName);
            string newFileName = Path.GetFileName(newOriginPath);

            var toUpdate = _records.Values
                .Where(r => string.Equals(r.OriginClipPath, oldOriginPathOrName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(r.OriginClipFileName, oldFileName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (toUpdate.Count > 0)
            {
                foreach (var oldEntry in toUpdate)
                {
                    var updated = oldEntry with
                    {
                        OriginClipFileName = newFileName,
                        OriginClipPath = newOriginPath
                    };
                    _records[updated.ClipFileName] = updated;
                    _records[updated.ClipPath] = updated;
                }
                Save();
            }
        }
    }

    public IReadOnlyDictionary<string, DeduplicationEntry> GetAllRecords()
    {
        lock (_lock)
        {
            return new Dictionary<string, DeduplicationEntry>(_records, StringComparer.OrdinalIgnoreCase);
        }
    }

    public void PruneOrphanedRecords(Func<string, bool> fileExistsFunc)
    {
        lock (_lock)
        {
            var keysToRemove = new List<string>();
            foreach (var kvp in _records)
            {
                var entry = kvp.Value;
                bool clipExists = fileExistsFunc(entry.ClipPath) || fileExistsFunc(entry.ClipFileName);
                bool originExists = fileExistsFunc(entry.OriginClipPath) || fileExistsFunc(entry.OriginClipFileName);

                if (!clipExists || !originExists)
                {
                    keysToRemove.Add(kvp.Key);
                }
            }

            if (keysToRemove.Count > 0)
            {
                foreach (var k in keysToRemove)
                {
                    _records.Remove(k);
                }
                Save();
            }
        }
    }

    public void ImportRemoteRecords(IReadOnlyDictionary<string, DeduplicationEntry>? remoteRecords)
    {
        if (remoteRecords == null) return;
        lock (_lock)
        {
            foreach (var kvp in remoteRecords)
            {
                _records[kvp.Key] = kvp.Value;
                if (!string.IsNullOrEmpty(kvp.Value.ClipFileName))
                    _records[kvp.Value.ClipFileName] = kvp.Value;
                if (!string.IsNullOrEmpty(kvp.Value.ClipPath))
                    _records[kvp.Value.ClipPath] = kvp.Value;
            }
        }
    }

    public void Load()
    {
        try
        {
            if (File.Exists(DeduplicatedFilePath))
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, DeduplicationEntry>>(File.ReadAllText(DeduplicatedFilePath));
                if (dict is not null)
                {
                    lock (_lock)
                    {
                        _records = new Dictionary<string, DeduplicationEntry>(dict, StringComparer.OrdinalIgnoreCase);
                    }
                }
            }
        }
        catch { }
    }

    public void Save()
    {
        try
        {
            string? dir = Path.GetDirectoryName(DeduplicatedFilePath);
            if (dir is not null) Directory.CreateDirectory(dir);

            Dictionary<string, DeduplicationEntry> copy;
            lock (_lock)
            {
                copy = new Dictionary<string, DeduplicationEntry>(_records, StringComparer.OrdinalIgnoreCase);
            }

            var opt = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(DeduplicatedFilePath, JsonSerializer.Serialize(copy, opt));
        }
        catch { }
    }
}
