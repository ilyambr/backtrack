using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Backtrack.Streaming;
using Backtrack.Updates;
using Microsoft.Win32;
using LibVlc = LibVLCSharp.Shared;

namespace Backtrack;

public partial class MainWindow : Window
{
    private readonly RecentClipsOverlay _recentClipsOverlay;


        private void InitializeRecentClipsOverlay()
    {
        _recentClipsOverlay.PositionChanged += (x, y) =>
        {
            _settings.RecentClipsOverlayX = x;
            _settings.RecentClipsOverlayY = y;
            _settings.Save();
        };
    }


    private void PositionRecentClipsOverlay()
    {
        if (_settings.RecentClipsOverlayX is double x && _settings.RecentClipsOverlayY is double y)
        {
            _recentClipsOverlay.Left = x;
            _recentClipsOverlay.Top = y;
            return;
        }

        
        
        
        
        
        
        
        
        
        PositionInBottomRightCorner();
        void Handler(object? s, SizeChangedEventArgs e)
        {
            _recentClipsOverlay.SizeChanged -= Handler;
            PositionInBottomRightCorner();
        }
        _recentClipsOverlay.SizeChanged += Handler;
    }


        private void UpdateRecentClipsOverlayVisibility(Screen currentScreen)
    {
        if (!_settings.ShowRecentClipsOverlay || !IsVisible || currentScreen != Screen.Idle)
        {
            _recentClipsOverlay.Hide();
            return;
        }

        
        
        
        
        
        
        RefreshRecentClipsOverlay();
        PositionRecentClipsOverlay();
        _recentClipsOverlay.Show();
    }


        private void RefreshRecentClipsOverlay()
    {
        if (!_settings.ShowRecentClipsOverlay)
            return;

        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            _ = RefreshRecentClipsOverlayRemoteAsync();
            return;
        }

        try
        {
            if (!Directory.Exists(_settings.ClipsFolder))
                return;

            List<FileInfo> recent = Directory.EnumerateFiles(_settings.ClipsFolder, "*.*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())
                            && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                .OrderByDescending(f => f.LastWriteTime)
                .Take(4)
                .ToList();

            _recentClipsOverlay.SetTiles(recent.Select(BuildRecentClipTile));
        }
        catch
        {
            
            
        }
    }


        private async Task RefreshRecentClipsOverlayRemoteAsync()
    {
        List<(string RelativePath, RemoteGalleryFile File)>? all = await ListAllRemoteClipsAsync();
        if (all is null)
            return;

        List<(string RelativePath, RemoteGalleryFile File)> recent = all
            .Where(t => !_pendingRemoteDeletePaths.Contains(t.RelativePath))
            .OrderByDescending(t => t.File.Modified)
            .Take(4)
            .ToList();

        _recentClipsOverlay.SetTiles(recent.Select(t => BuildRecentRemoteClipTile(t.RelativePath, t.File)));
    }


    private Border BuildRecentClipTile(FileInfo file)
    {
        var thumbImage = new Image { Stretch = Stretch.UniformToFill };
        var thumb = new Border { Background = (Brush)FindResource("ThumbnailBg"), Width = 96, Height = 64, Cursor = Cursors.Hand, ClipToBounds = true, Child = thumbImage };
        thumb.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            ShowMainWindowAndOpenInPlayer(file);
        };

        var title = new TextBlock
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Foreground = (Brush)FindResource("Text0"),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Width = 96,
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.IBeam,
            ToolTip = "Double-click to rename",
        };
        title.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                e.Handled = true;
                BeginRecentClipRename(title, file);
            }
        };

        DateTime modified = file.LastWriteTime;
        string dateText = modified.Date == DateTime.Today ? modified.ToString("h:mm tt") : modified.ToString("MMM d, h:mm tt");
        var sub = new TextBlock
        {
            Text = $"{dateText} · {FormatFileSize(file.Length)}",
            FontSize = 9.5,
            Foreground = (Brush)FindResource("Text2"),
            Width = 96,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var content = new StackPanel { Margin = new Thickness(0, 0, 10, 0) };
        content.Children.Add(thumb);
        content.Children.Add(title);
        content.Children.Add(sub);

        var tile = new Border { Child = content };
        _ = LoadThumbnailAndPruneIfGlitchedAsync(file, thumbImage, tile);

        
        
        
        
        
        var contextMenu = new ContextMenu { Style = (Style)FindResource("DarkContextMenu") };
        var openFolderItem = new MenuItem { Header = "Open file location", Style = (Style)FindResource("DarkMenuItem") };
        openFolderItem.Click += (_, _) => RevealInExplorerAndClose(file.FullName);
        var renameItem = new MenuItem { Header = "Rename", Style = (Style)FindResource("DarkMenuItem") };
        renameItem.Click += (_, _) => BeginRecentClipRename(title, file);
        var copyPathItem = new MenuItem { Header = "Copy path", Style = (Style)FindResource("DarkMenuItem") };
        copyPathItem.Click += (_, _) => Clipboard.SetText(file.FullName);
        var deleteItem = new MenuItem { Header = "Delete", Style = (Style)FindResource("DarkMenuItem"), Foreground = (Brush)FindResource("Rec") };
        deleteItem.Click += (_, _) => QueueDeleteWithUndo(file);
        contextMenu.Items.Add(openFolderItem);
        contextMenu.Items.Add(renameItem);
        contextMenu.Items.Add(copyPathItem);
        contextMenu.Items.Add(deleteItem);
        thumb.ContextMenu = contextMenu;

        return tile;
    }


    private void BeginRecentClipRename(TextBlock title, FileInfo file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Width = 96,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; RestoreTitle(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRename(); } };

        void RestoreTitle()
        {
            int i = parent.Children.IndexOf(box);
            if (i >= 0)
            {
                parent.Children.RemoveAt(i);
                parent.Children.Insert(i, title);
            }
        }

        void CommitRename()
        {
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                try
                {
                    string newPath = Path.Combine(file.DirectoryName!, newName + file.Extension);
                    File.Move(file.FullName, newPath);
                    title.Text = newName;
                    RefreshRecentClipsOverlay();
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, $"Couldn't rename: {ex.Message}", "Backtrack");
                }
            }
            RestoreTitle();
        }
    }


    private void BeginRecentRemoteClipRename(TextBlock title, string relativePath, RemoteGalleryFile file)
    {
        if (title.Parent is not Panel parent)
            return;
        int index = parent.Children.IndexOf(title);
        if (index < 0)
            return;

        bool finished = false;

        var box = new TextBox
        {
            Text = Path.GetFileNameWithoutExtension(file.Name),
            FontWeight = FontWeights.Bold,
            FontSize = 10.5,
            Width = 96,
            Margin = title.Margin,
            Background = (Brush)FindResource("RowBg"),
            Foreground = (Brush)FindResource("Text0"),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
        };

        parent.Children.RemoveAt(index);
        parent.Children.Insert(index, box);

        box.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { if (!finished) { finished = true; CommitRemoteRename(); } }
            else if (e.Key == Key.Escape) { e.Handled = true; if (!finished) { finished = true; RestoreTitle(); } }
        };
        box.LostFocus += (_, _) => { if (!finished) { finished = true; CommitRemoteRename(); } };

        void RestoreTitle()
        {
            int i = parent.Children.IndexOf(box);
            if (i >= 0)
            {
                parent.Children.RemoveAt(i);
                parent.Children.Insert(i, title);
            }
        }

        async void CommitRemoteRename()
        {
            string newName = box.Text.Trim();
            if (!string.IsNullOrEmpty(newName) && newName != Path.GetFileNameWithoutExtension(file.Name))
            {
                (bool success, string? error, _) = await _pairing.RenameRemoteClipAsync(relativePath, newName);
                if (success)
                {
                    title.Text = newName;
                    _ = RefreshRecentClipsOverlayRemoteAsync();
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    return;
                }
                else
                {
                    MessageBox.Show(this, $"Couldn't rename: {error}", "Backtrack");
                }
            }
            RestoreTitle();
        }
    }


        private async Task LoadThumbnailAndPruneIfGlitchedAsync(FileInfo file, Image thumbImage, Border tile)
    {
        await LoadThumbnailAsync(file, thumbImage);
        if (TryGetCachedDurationMs(file) is < 2000)
            RefreshRecentClipsOverlay();
    }


    public void ToggleVisible()
    {
        if (IsVisible)
        {
            CloseOverlay();
        }
        else
        {
            _scrim.ArmDismissCooldown(400);
            if (_settings.EnableAnimations)
            {
                FadeWindowIn(_scrim);
                _logo.ShowWithIntro();
                FadeWindowIn(this);
            }
            else
            {
                _scrim.Show();
                _logo.ShowWithIntro();
                Show();
            }
            Activate();
            
            
            
            
            
            
            _statusOverlay.IsHudOpen = true;
            _statusOverlay.Reposition();
            if (_settings.ShowStatusIndicator)
            {
                _statusOverlay.Show();
                WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_statusOverlay).Handle);
            }
            _toastOverlay.Show();
            _toastOverlay.UpdatePosition(true);
            WindowZOrder.BringToFrontWithoutActivating(new WindowInteropHelper(_toastOverlay).Handle);
            RefreshUpdatePromptVisibility();
            RefreshOverlayLogVisibilityAndMode();

            if (_settings.ShowDisclaimer)
                _disclaimer.Show();

            
            
            
            
            
            
            
            
            UpdateRecentClipsOverlayVisibility(_lastScreen);

            
            
            UpdateStreamingBoxVisibility();
        }
    }


    private async Task DeleteOrRecycleCancelledFileAsync(string path)
    {
        string fullPath = Path.GetFullPath(path);
        _pendingDeletePaths.Add(fullPath);
        Dispatcher.Invoke(() =>
        {
            RefreshRecentClipsOverlay();
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            else
                _ = RefreshGalleryCountAsync();
        });

        try
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                if (!File.Exists(path))
                    break;
                try
                {
                    if (RecycleBin.Delete(path))
                        break;
                    File.Delete(path);
                    break;
                }
                catch
                {
                    await Task.Delay(100);
                }
            }
        }
        finally
        {
            _pendingDeletePaths.Remove(fullPath);
            Dispatcher.Invoke(() =>
            {
                RefreshRecentClipsOverlay();
                if (GalleryPanel.Visibility == Visibility.Visible)
                    LoadGallery();
                else
                    _ = RefreshGalleryCountAsync();
            });
        }
    }


        private void QueueRemoteDeleteWithUndo(string relativePath, string displayName, RemoteGalleryFile? file)
    {
        _pendingRemoteDeletePaths.Add(relativePath);
        if (GalleryPanel.Visibility == Visibility.Visible)
            LoadGallery();
        
        
        
        
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowDeleteUndo(displayName,
            onExpire: () =>
            {
                _pendingRemoteDeletePaths.Remove(relativePath);
                _ = FinishRemoteDeleteAsync(relativePath, displayName, file);
            },
            onUndo: () =>
            {
                _pendingRemoteDeletePaths.Remove(relativePath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }


    private void QueueDeleteWithUndo(FileInfo file)
    {
        string fullPath = Path.GetFullPath(file.FullName);
        _pendingDeletePaths.Add(fullPath);
        LoadGallery();
        
        
        
        
        
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowDeleteUndo(file.Name,
            onExpire: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                if (!RecycleBin.Delete(fullPath))
                {
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"Couldn't delete \"{file.Name}\".", "Backtrack"));
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    else
                        _ = RefreshGalleryCountAsync();
                    RefreshRecentClipsOverlay();
                });
            },
            onUndo: () =>
            {
                _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }


        private void QueueMultiDeleteWithUndo(List<FileInfo> files)
    {
        var fullPaths = files.Select(f => Path.GetFullPath(f.FullName)).ToList();
        foreach (string fullPath in fullPaths)
            _pendingDeletePaths.Add(fullPath);
        LoadGallery();
        
        RefreshRecentClipsOverlay();

        _toastOverlay.ShowMultiDeleteUndo(files.Count,
            onExpire: () =>
            {
                var failed = new List<string>();
                foreach (string fullPath in fullPaths)
                {
                    _pendingDeletePaths.Remove(fullPath);
                    if (!RecycleBin.Delete(fullPath))
                        failed.Add(Path.GetFileName(fullPath));
                }
                if (failed.Count > 0)
                {
                    Dispatcher.BeginInvoke(() => MessageBox.Show(this,
                        $"Couldn't delete {failed.Count} clip(s): {string.Join(", ", failed)}.", "Backtrack"));
                }
                Dispatcher.BeginInvoke(() =>
                {
                    if (GalleryPanel.Visibility == Visibility.Visible)
                        LoadGallery();
                    else
                        _ = RefreshGalleryCountAsync();
                    RefreshRecentClipsOverlay();
                });
            },
            onUndo: () =>
            {
                foreach (string fullPath in fullPaths)
                    _pendingDeletePaths.Remove(fullPath);
                Dispatcher.BeginInvoke(() =>
                {
                    LoadGallery();
                    RefreshRecentClipsOverlay();
                });
            });
    }


        private async Task RunRemoteTrimAsync(bool replaceOriginal)
    {
        (string relPath, string _) = _currentPlayerRemoteOrigin!.Value;
        TimeSpan start = _trimStart!.Value;
        TimeSpan end = _trimEnd!.Value;
        AppLog.Write($"[trim_clip] RunRemoteTrimAsync entered: path='{relPath}' {start}-{end} replace={replaceOriginal}");

        if (replaceOriginal)
        {
            bool? userConfirmed = null;
            ShowConfirmDialog("Replace the original clip with this trimmed version? This can't be undone.", "Replace", c => userConfirmed = c);
            while (!userConfirmed.HasValue && IsVisible)
                await Task.Delay(50);
            if (userConfirmed != true)
            {
                AppLog.Write("[trim_clip] replace not confirmed -- aborted");
                return;
            }
        }

        DetachPlayerVideo();
        DisposeVlcPlayerAsync();

        _isTrimming = true;
        TrimReplaceButton.IsEnabled = false;
        TrimSaveNewButton.IsEnabled = false;
        TrimStatusText.Text = $"Trimming on {_settings.PairedPeerName}'s PC...";

        try
        {
            (bool success, string? error, string? newPath, long trimmedSize) = await _pairing.TrimRemoteClipAsync(relPath, start, end, replaceOriginal);
            AppLog.Write(success ? $"[trim_clip] RunRemoteTrimAsync: succeeded (size {trimmedSize} bytes)" : $"[trim_clip] RunRemoteTrimAsync: failed -- {error}");
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't trim on {_settings.PairedPeerName}'s PC: {error}", "Backtrack");
                return;
            }

            TrimPanel.Visibility = Visibility.Collapsed;
            PlayerTransportRow.Visibility = Visibility.Visible;
            MoveTransportControlsForTrim(intoTrimRow: false);

            string openedRelPath = newPath ?? relPath;
            _toastOverlay.ShowTrimSaved(openedRelPath);

            _ = RefreshGalleryCountAsync();
            RefreshRecentClipsOverlay();

            var remoteFile = new RemoteGalleryFile(
                Name: Path.GetFileName(openedRelPath),
                Size: trimmedSize,
                Modified: DateTime.UtcNow
            );
            OpenRemoteClipStreaming(openedRelPath, remoteFile);
        }
        finally
        {
            _isTrimming = false;
            TrimReplaceButton.IsEnabled = true;
            TrimSaveNewButton.IsEnabled = true;
        }
    }


    

    private void LoadSettingsUi()
    {
        
        
        
        
        BuildThemeSwatches();
        RefreshThemeSwatchSelection();
        EnableAnimationsToggle.IsChecked = _settings.EnableAnimations;

        DiagnosticLogToggle.IsChecked = _settings.DiagnosticLogEnabled;
        OpenDiagnosticLogButton.Visibility = _settings.DiagnosticLogEnabled ? Visibility.Visible : Visibility.Collapsed;

        
        
        
        
        
        if (!_settings.DeveloperModeAutoSuggested)
        {
            _settings.DeveloperModeAutoSuggested = true;
            _settings.Save();
            if (UpdateService.IsRunningFromDevLocation)
            {
                SetDeveloperModeEnabled(true);
                DeveloperModeLockedNoteText.Visibility = Visibility.Visible;
            }
        }
        DeveloperModeToggle.IsChecked = _settings.DeveloperModeEnabled;

        DisableHardwareAccelToggle.IsChecked = _settings.DisableHardwareAcceleration;

        ShowRecentClipsToggle.IsChecked = _settings.ShowRecentClipsOverlay;
        LaunchWithWindowsToggle.IsChecked = _settings.LaunchWithWindows;
        ClipsFolderText.Text = _settings.ClipsFolder;
        BufferDurationSlider.Value = _settings.ReplayBufferMinutes;
        RefreshBufferDurationUi();

        BuffersSection.Visibility = _settings.ObsIsRemote ? Visibility.Collapsed : Visibility.Visible;
        RecordingsSection.Visibility = _settings.ObsIsRemote ? Visibility.Collapsed : Visibility.Visible;

        ObsRemoteToggle.IsChecked = _settings.ObsIsRemote;
        ObsRemoteFields.Visibility = _settings.ObsIsRemote ? Visibility.Visible : Visibility.Collapsed;
        ObsHostBox.Text = _settings.ObsHost;
        ObsPortBox.Text = _settings.ObsPort.ToString();
        ObsPasswordBox.Password = _settings.ObsRemotePassword;

        ShowDisclaimerToggle.IsChecked = _settings.ShowDisclaimer;
        DisableAudioCuesToggle.IsChecked = _settings.DisableAudioCues;
        if (AudioCueVolumeRow != null && AudioCueVolumeSlider != null && AudioCueVolumeText != null)
        {
            AudioCueVolumeRow.Opacity = _settings.DisableAudioCues ? 0.5 : 1.0;
            AudioCueVolumeSlider.ValueChanged -= AudioCueVolumeSlider_ValueChanged;
            AudioCueVolumeSlider.Value = Math.Clamp(_settings.AudioCueVolume, 0, 100);
            AudioCueVolumeSlider.IsEnabled = !_settings.DisableAudioCues;
            AudioCueVolumeSlider.ValueChanged += AudioCueVolumeSlider_ValueChanged;
            AudioCueVolumeText.Text = $"{Math.Clamp(_settings.AudioCueVolume, 0, 100)}%";
        }
        ShowStatusIndicatorToggle.IsChecked = _settings.ShowStatusIndicator;
        
        
        
        DefaultAudioTrackSelector.SelectionChanged -= DefaultAudioTrackSelector_SelectionChanged;
        DefaultAudioTrackSelector.SelectedIndex = Math.Clamp(_settings.DefaultPlayerAudioTrackIndex, 0, 6);
        DefaultAudioTrackSelector.SelectionChanged += DefaultAudioTrackSelector_SelectionChanged;

        
        
        
        
        StatusIndicatorOrientationSelector.SelectionChanged -= StatusIndicatorOrientationSelector_SelectionChanged;
        StatusIndicatorOrientationSelector.SelectedIndex = _settings.StatusIndicatorOrientation == StatusIndicatorOrientation.Vertical ? 1 : 0;
        StatusIndicatorOrientationSelector.SelectionChanged += StatusIndicatorOrientationSelector_SelectionChanged;

        StatusIndicatorLocationSelector.SelectionChanged -= StatusIndicatorLocationSelector_SelectionChanged;
        StatusIndicatorLocationSelector.SelectedIndex = (int)_settings.StatusIndicatorLocation;
        StatusIndicatorLocationSelector.SelectionChanged += StatusIndicatorLocationSelector_SelectionChanged;

        UpdateStatusIndicatorPreview();

        
        
        
        
        
        
        
        
        
        
        if (_settings.DeveloperModeEnabled && !_settings.DisableBacktrackAutoUpdate)
        {
            _settings.DisableBacktrackAutoUpdate = true;
            _settings.Save();
        }
        DisableBacktrackAutoUpdateToggle.IsChecked = _settings.DisableBacktrackAutoUpdate;
        
        
        
        
        DisableBacktrackAutoUpdateToggle.IsEnabled = !_settings.DeveloperModeEnabled;

        
        
        
        if (_settings.ObsIsRemote && !_settings.DisablePluginAutoUpdate)
        {
            _settings.DisablePluginAutoUpdate = true;
            _settings.Save();
        }
        DisablePluginAutoUpdateToggle.IsChecked = _settings.ObsIsRemote || _settings.DisablePluginAutoUpdate;
        DisablePluginAutoUpdateToggle.IsEnabled = !_settings.ObsIsRemote;
        HotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.HotkeyModifiers, (uint)_settings.HotkeyVirtualKey);
        CancelRecordHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.CancelRecordHotkeyModifiers, (uint)_settings.CancelRecordHotkeyVirtualKey);
        BookmarkHotkeyCaptureButton.Content = FormatHotkey((GlobalHotkey.Modifiers)_settings.BookmarkHotkeyModifiers, (uint)_settings.BookmarkHotkeyVirtualKey);

        LoadDisplaySelector();

        ShareClipsToggle.IsChecked = _settings.ShareClipsEnabled;
        RefreshShareClipsUi();
        RefreshPairingStatusUi();
        RenderDiscoveredDevices();

        RamDiskToggle.IsChecked = _settings.RamDiskEnabled;
        RamDiskFields.Visibility = _settings.RamDiskEnabled ? Visibility.Visible : Visibility.Collapsed;
        RamDiskDriveBox.Text = _settings.RamDiskDriveLetter.ToString();
        RamDiskSizeBox.Text = _settings.RamDiskSizeMb.ToString();
        RefreshRamDiskStatusText();

        StorageLimitToggle.IsChecked = _settings.StorageLimitEnabled;
        StorageLimitFields.Visibility = _settings.StorageLimitEnabled ? Visibility.Visible : Visibility.Collapsed;
        StorageLimitGbBox.Text = _settings.StorageLimitGb.ToString("0.#");
        RefreshStorageLimitStatusText();

        AutoDeleteOldClipsToggle.IsChecked = _settings.AutoDeleteOldClipsEnabled;
        AutoDeleteOldClipsFields.Visibility = _settings.AutoDeleteOldClipsEnabled ? Visibility.Visible : Visibility.Collapsed;
        AutoDeleteOldClipsDaysBox.Text = _settings.AutoDeleteOldClipsAfterDays.ToString();

        OverlayLogToggle.IsChecked = _settings.OverlayLogEnabled;
        OverlayLogModeFields.Visibility = _settings.OverlayLogEnabled ? Visibility.Visible : Visibility.Collapsed;
        
        
        
        
        OverlayLogModeSelector.SelectionChanged -= OverlayLogModeSelector_SelectionChanged;
        OverlayLogModeSelector.SelectedIndex = _settings.OverlayLogMode == "Backtrack" ? 1 : 0;
        OverlayLogModeSelector.SelectionChanged += OverlayLogModeSelector_SelectionChanged;

        
        
        
        
        
        
        
    }


    private void DisplaySelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DisplaySelector.SelectedValue is not string deviceName)
            return;

        string? previousDeviceName = _settings.DisplayDeviceName;
        _settings.DisplayDeviceName = deviceName;
        _settings.Save();

        
        
        
        
        ShowScreen(Screen.Settings);
        _statusOverlay.Reposition();
        _scrim.Reposition();
        _disclaimer.Reposition();
        _logo.Reposition();
        _toastOverlay.UpdatePosition(true);

        
        if (_settings.ShowRecentClipsOverlay)
        {
            RefreshRecentClipsOverlay();
            RepositionRecentClipsOverlayForDisplayChange(previousDeviceName);
        }
    }


        private void RepositionRecentClipsOverlayForDisplayChange(string? previousDeviceName)
    {
        if (_settings.RecentClipsOverlayX is not double x || _settings.RecentClipsOverlayY is not double y)
        {
            PositionRecentClipsOverlay();
            return;
        }

        Rect oldBounds = DisplayMonitors.ResolveBoundsDiu(previousDeviceName);
        Rect newBounds = TargetScreenBounds; 

        double relativeX = oldBounds.Width > 0 ? (x - oldBounds.X) / oldBounds.Width : 0;
        double relativeY = oldBounds.Height > 0 ? (y - oldBounds.Y) / oldBounds.Height : 0;

        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        double newX = newBounds.X + relativeX * newBounds.Width;
        double newY = newBounds.Y + relativeY * newBounds.Height;
        
        
        
        double clampedX = Math.Clamp(newX, newBounds.X, Math.Max(newBounds.X, newBounds.X + newBounds.Width - width));
        double clampedY = Math.Clamp(newY, newBounds.Y, Math.Max(newBounds.Y, newBounds.Y + newBounds.Height - height));

        _recentClipsOverlay.Left = clampedX;
        _recentClipsOverlay.Top = clampedY;
        _settings.RecentClipsOverlayX = clampedX;
        _settings.RecentClipsOverlayY = clampedY;
        _settings.Save();
    }


        private void RepositionAllForDisplayChange()
    {
        try
        {
            
            
            
            
            
            
            if (IsVisible)
                ShowScreen(_lastScreen, skipEntranceAnimation: true);

            _statusOverlay.Reposition();
            _scrim.Reposition();
            _disclaimer.Reposition();
            _logo.Reposition();
            _toastOverlay.UpdatePosition(true);
            UpdateStreamingBoxVisibility();
            ClampRecentClipsOverlayOnScreen();
        }
        catch (Exception ex)
        {
            
            
            
            AppLog.WriteError("Reposition after display settings changed", ex);
        }
    }


        private void ClampRecentClipsOverlayOnScreen()
    {
        if (_settings.RecentClipsOverlayX is not double x || _settings.RecentClipsOverlayY is not double y)
            return;

        Rect bounds = TargetScreenBounds;
        double width = _recentClipsOverlay.ActualWidth > 0 ? _recentClipsOverlay.ActualWidth : 260;
        double height = _recentClipsOverlay.ActualHeight > 0 ? _recentClipsOverlay.ActualHeight : 100;
        double clampedX = Math.Clamp(x, bounds.X, Math.Max(bounds.X, bounds.X + bounds.Width - width));
        double clampedY = Math.Clamp(y, bounds.Y, Math.Max(bounds.Y, bounds.Y + bounds.Height - height));

        _recentClipsOverlay.Left = clampedX;
        _recentClipsOverlay.Top = clampedY;
        if (clampedX != x || clampedY != y)
        {
            _settings.RecentClipsOverlayX = clampedX;
            _settings.RecentClipsOverlayY = clampedY;
            _settings.Save();
        }
    }


    private void ShowRecentClipsToggle_Click(object sender, RoutedEventArgs e)
    {
        bool enabled = ShowRecentClipsToggle.IsChecked == true;
        _settings.ShowRecentClipsOverlay = enabled;

        
        
        
        
        
        
        
        if (!enabled)
        {
            _settings.RecentClipsOverlayX = null;
            _settings.RecentClipsOverlayY = null;
        }
        _settings.Save();

        
        
        
        
        
        
        UpdateRecentClipsOverlayVisibility(_lastScreen);
    }

}
