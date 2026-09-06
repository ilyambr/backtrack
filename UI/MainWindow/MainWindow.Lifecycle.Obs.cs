using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using Backtrack.Core;
using Backtrack.Interop;

namespace Backtrack;

public partial class MainWindow : Window
{
    private void WireObsEvents()
    {
        _obs.RecordingStateChanged += (active, path) => Dispatcher.BeginInvoke(async () =>
        {
            if (_cancellingMainRecording)
            {
                if (!active)
                {
                    _cancellingMainRecording = false;
                    string? dur = _cancellingMainRecordingDuration;
                    _cancellingMainRecordingDuration = null;
                    _activeRecordingMarkers.Clear();
                    if (path is not null)
                    {
                        try
                        {
                            await DeleteOrRecycleCancelledFileAsync(path);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write($"DeleteOrRecycleCancelledFileAsync skipped/failed: {ex.Message}");
                        }
                    }
                    _toastOverlay.ShowRecordingCancelled("Full Scene", dur);
                    AppLog.Write($"Main recording cancelled and recycled: '{path}'");
                }
                return;
            }

            if (active)
                AppLog.Write("Recording started");
            else if (path is not null)
            {
                if (File.Exists(path) && new FileInfo(path).Length < 10240)
                {
                    try { File.Delete(path); } catch { }
                    AppLog.Write($"Aborted empty recording removed: '{path}'");
                    return;
                }

                _toastOverlay.ShowRecording(active, path);
                if (_activeRecordingMarkers.Count > 0)
                {
                    string clipKey = Path.GetFileName(path);
                    var markers = new List<double>(_activeRecordingMarkers);
                    _activeRecordingMarkers.Clear();
                    SaveClipMarkers(clipKey, markers);
                }
                AppLog.Write($"Recording saved to '{path}'");
                ShowObsModeMessage($"Recording saved to '{path}'");
                RefreshRecentClipsOverlay();
                return;
            }

            _toastOverlay.ShowRecording(active, path);
        });
        _obs.StreamingStateChanged += active => Dispatcher.BeginInvoke(() =>
        {

            if (active == _isStreaming)
                return;
            _toastOverlay.ShowStreaming(active);
            AppLog.Write(active ? "Livestream started" : "Livestream ended");
            _isStreaming = active;
            _statusOverlay.SetStreaming(active);
            UpdateStreamingBoxVisibility();
        });

        _obs.VirtualCamStateChanged += active => Dispatcher.BeginInvoke(() =>
        {
            _statusOverlay.SetVirtualCamActive(active);
        });
        _obs.EncoderOverloadDetected += info => Dispatcher.BeginInvoke(() =>
        {

            _lastEncoderOverloadEventUtc = DateTime.UtcNow;

            if (DateTime.UtcNow - _lastEncoderOverloadToastUtc < TimeSpan.FromSeconds(30))
                return;
            _lastEncoderOverloadToastUtc = DateTime.UtcNow;

            var causes = new List<string>();
            if (info.MainStream)
                causes.Add("the stream (encoder or network)");
            if (info.MainRecording)
                causes.Add("the main recording");
            if (info.MainReplayBuffer)
                causes.Add("the main replay buffer");
            if (info.ThisFilter)
                causes.Add($"'{info.Filter}' on '{info.Source}'");
            if (causes.Count == 0)
                return;

            string summary = string.Join(", ", causes);
            _toastOverlay.ShowEncoderOverload(summary);
            AppLog.Write($"Encoder overload detected: {summary}");
        });

        async Task<string> ResolveRowLabelAsync(string key)
        {
            if (!_rowLabels.TryGetValue(key, out string? label))
            {

                await PrefetchRowLabelsAsync();
                _rowLabels.TryGetValue(key, out label);
            }
            label ??= key;
            return DisplayLabel(label);
        }

        _obs.ReplaySaving += key => Dispatcher.BeginInvoke(async () =>
        {
            var replayRows = await _obs.ListReplayRowsAsync();
            var match = replayRows.FirstOrDefault(r => r.Key == key);
            if (match is not null && match.Status == 0)
            {

                return;
            }
            string label = await ResolveRowLabelAsync(key);
            _toastOverlay.ShowProcessingClip(key, label);
        });
        _obs.ReplaySaved += (key, path) => Dispatcher.BeginInvoke(async () =>
        {
            string label = await ResolveRowLabelAsync(key);

            if (!string.IsNullOrEmpty(path))
            {
                string clipKey = Path.GetFileName(path);
                var file = new FileInfo(path);

                if (_activeRecordingMarkers.Count > 0)
                {
                    var markers = new List<double>(_activeRecordingMarkers);
                    _activeRecordingMarkers.Clear();
                    SaveClipMarkers(clipKey, markers);
                }
                else if (_pendingBookmarkUtcTimes.Count > 0)
                {
                    await EnsureThumbnailCachedAsync(file);
                    long? durationMs = TryGetCachedDurationMs(file);
                    double clipDurationSec = durationMs.HasValue && durationMs.Value > 0 ? durationMs.Value / 1000.0 : 60.0;

                    DateTime saveTimeUtc = DateTime.UtcNow;
                    var markers = new List<double>();

                    foreach (DateTime bookmarkUtc in _pendingBookmarkUtcTimes)
                    {
                        double elapsedSinceBookmark = (saveTimeUtc - bookmarkUtc).TotalSeconds;
                        if (elapsedSinceBookmark >= 0 && elapsedSinceBookmark <= (clipDurationSec + 15.0))
                        {
                            double bookmarkPosSec = Math.Max(0, clipDurationSec - elapsedSinceBookmark);
                            markers.Add(bookmarkPosSec);
                        }
                    }

                    _pendingBookmarkUtcTimes.Clear();

                    if (markers.Count > 0)
                    {
                        SaveClipMarkers(clipKey, markers);
                    }
                }
            }

            string? customSub = null;
            var dedupEntry = DeduplicationService.Instance.RegisterSavedClip(key, path);
            if (dedupEntry != null)
            {
                string durStr = FormatDuration(dedupEntry.DurationSeconds * 1000L);
                string folder = !string.IsNullOrEmpty(path) ? (Path.GetDirectoryName(path) ?? path) : _settings.ClipsFolder;
                customSub = $"Saved {durStr} at '{folder}'";
            }

            _toastOverlay.CompleteProcessingClip(key, label, path, customSub);
            AppLog.Write($"{label} saved to '{path}'");
            _ = RefreshGalleryCountAsync();
            RefreshRecentClipsOverlay();
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        });

        PlayerSeekTrack.SizeChanged += (_, _) => { if (PlayerPanel.Visibility == Visibility.Visible) RenderPlayerMarkers(); };
        TrimTimelineTrack.SizeChanged += (_, _) => { if (PlayerPanel.Visibility == Visibility.Visible) RenderPlayerMarkers(); };
        _obs.StateChanged += () => Dispatcher.BeginInvoke(() =>
        {
            AppLog.Write(_obs.IsConnected ? "Connected to OBS" : "Disconnected from OBS");
            _ = PrefetchRowLabelsAsync();
            if (_settings.RamDiskEnabled && RamDisk.IsMounted(_settings.RamDiskDriveLetter))
                _ = PushRamDiskDestDirAsync();
            else if (!_settings.RamDiskEnabled)
                _ = _obs.RevertSourceRecordFilterPathsAsync(_settings.RamDiskDriveLetter, _settings.ClipsFolder);
        });

    }

}
