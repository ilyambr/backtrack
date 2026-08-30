using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Backtrack.Core;
using Backtrack.Obs;

namespace Backtrack.StreamDeck;

public sealed partial class StreamDeckIpcServer
{
    private readonly ConcurrentDictionary<string, DateTime> _lastReplaySaveUtc = new(StringComparer.OrdinalIgnoreCase);
    private int _lastObservedClipLength = 0;

    public void ClearLastSaveTimestamps()
    {
        _lastReplaySaveUtc.Clear();
    }

    public async Task<bool> ExecuteActionAsync(string action, string source, int duration = 0)
    {
        try
        {
            switch (action.ToLowerInvariant())
            {
                case "save_replay":
                case "clip":
                    return await SaveReplayAsync(source);

                case "toggle_record":
                case "record":
                    return await ToggleRecordAsync(source);

                case "cancel_recording":
                case "cancel":
                    return await CancelRecordingAsync(source);

                case "add_bookmark":
                case "bookmark":
                    _addBookmarkAction?.Invoke();
                    _ = BroadcastAsync("bookmark_added", new { timestamp = DateTime.UtcNow });
                    return true;

                case "toggle_hud":
                case "hud":
                    _toggleHudAction.Invoke();
                    return true;

                case "set_clip_duration":
                case "set_duration":
                    return await SetClipDurationAsync(source, duration);

                case "get_state":
                case "snapshot":
                    _ = BroadcastStateSnapshotAsync();
                    return true;
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"[StreamDeck] Action '{action}' failed: {ex.Message}");
        }
        return false;
    }

    private async Task<bool> SetClipDurationAsync(string source, int duration)
    {
        if (duration <= 0)
        {
            if (int.TryParse(source, out int parsedSec) && parsedSec > 0)
            {
                duration = parsedSec;
                source = "main";
            }
            else
            {
                return false;
            }
        }

        _settings.PreferredClipLengthSeconds = duration;
        _settings.Save();
        _onClipDurationChanged?.Invoke(duration);

        if (_obs.IsConnected)
        {
            var allRows = await _obs.ListReplayRowsAsync();
            foreach (var r in allRows)
            {
                try { await _obs.SetReplayRowLengthAsync(r.Key, duration); } catch { }
            }
            try { await _obs.SetReplayBufferDurationAsync(duration); } catch { }
        }

        _ = BroadcastStateSnapshotAsync();
        return true;
    }

    private async Task<bool> SaveReplayAsync(string source)
    {
        if (!_obs.IsConnected) return false;

        var allRows = await _obs.ListReplayRowsAsync();
        ReplayRow? targetRow = null;

        if (string.IsNullOrWhiteSpace(source) || source.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            targetRow = allRows.FirstOrDefault(r => r.Status == 1) ?? allRows.FirstOrDefault();
        }
        else
        {
            targetRow = allRows.FirstOrDefault(r => NormalizeName(r.Label, _settings).Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                                    r.Label.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                                    r.Key.Equals(source, StringComparison.OrdinalIgnoreCase));
        }

        if (targetRow != null)
        {
            int preferredSeconds = _settings.PreferredClipLengthSeconds > 0 ? _settings.PreferredClipLengthSeconds : 60;

            if (_lastObservedClipLength > 0 && _lastObservedClipLength != preferredSeconds)
            {
                _lastReplaySaveUtc.Clear();
            }
            _lastObservedClipLength = preferredSeconds;

            bool isShortenedBackToBack = false;

            if (_lastReplaySaveUtc.TryGetValue(targetRow.Key, out DateTime lastSave))
            {
                double elapsed = (DateTime.UtcNow - lastSave).TotalSeconds;
                if (elapsed > 1 && elapsed < preferredSeconds)
                {
                    int effectiveSeconds = (int)Math.Ceiling(elapsed);
                    AppLog.Write($"[StreamDeck] Smart deduplication for {targetRow.Label}: clipping {effectiveSeconds}s since last save");
                    try { await _obs.SetReplayRowLengthAsync(targetRow.Key, effectiveSeconds); isShortenedBackToBack = true; } catch { }
                }
                else if (preferredSeconds > 0)
                {
                    try { await _obs.SetReplayRowLengthAsync(targetRow.Key, preferredSeconds); } catch { }
                }
            }
            else if (preferredSeconds > 0)
            {
                try { await _obs.SetReplayRowLengthAsync(targetRow.Key, preferredSeconds); } catch { }
            }

            _lastReplaySaveUtc[targetRow.Key] = DateTime.UtcNow;
            await _obs.SaveReplayRowAsync(targetRow.Key);

            if (isShortenedBackToBack && preferredSeconds > 0)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(1500);
                    try { await _obs.SetReplayRowLengthAsync(targetRow.Key, preferredSeconds); } catch { }
                });
            }

            return true;
        }

        return false;
    }

    private async Task<bool> ToggleRecordAsync(string source)
    {
        if (!_obs.IsConnected) return false;

        if (string.IsNullOrWhiteSpace(source) || source.Equals("main", StringComparison.OrdinalIgnoreCase))
        {
            await _obs.ToggleRecordAsync();
            return true;
        }

        var recRows = await _obs.ListRecordRowsAsync();
        var match = recRows.FirstOrDefault(r => NormalizeName(r.Label, _settings).Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                               r.Label.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                               r.SourceName.Equals(source, StringComparison.OrdinalIgnoreCase) ||
                                               r.Key.Equals(source, StringComparison.OrdinalIgnoreCase));
        if (match != null)
        {
            if (match.Status == 2)
                await _obs.StopRecordRowAsync(match.Key);
            else
                await _obs.StartRecordRowAsync(match.Key);
            return true;
        }

        return false;
    }

    private async Task<bool> CancelRecordingAsync(string? source = null)
    {
        if (!_obs.IsConnected) return false;

        var recRows = await _obs.ListRecordRowsAsync();
        bool anyCancelled = false;

        foreach (var r in recRows.Where(r => r.Status == 2))
        {
            try
            {
                await _obs.CancelRecordRowAsync(r.Key);
                anyCancelled = true;
            }
            catch { }
        }

        var recStatus = await _obs.GetRecordStatusAsync();
        if (recStatus.Active)
        {
            try
            {
                await _obs.StopMainRecordAsync();
                anyCancelled = true;
            }
            catch { }
        }

        return anyCancelled;
    }
}
