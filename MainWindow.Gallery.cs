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
    private string GalleryFolder => _currentGalleryFolder ?? _settings.ClipsFolder;

    private void GalleryStarredFilter_Click(object sender, RoutedEventArgs e)
    {
        if (_settings is null) return;
        _settings.GalleryStarredOnly = GalleryStarredFilterButton.IsChecked == true;
        _settings.Save();
        LoadGallery();
    }

    private void SyncGalleryToolbarUi()
    {
        if (_settings is null || GalleryStarredFilterButton is null || GallerySortComboBox is null) return;
        GalleryStarredFilterButton.IsChecked = _settings.GalleryStarredOnly;
        foreach (var item in GallerySortComboBox.Items)
        {
            if (item is ComboBoxItem cbi && cbi.Tag is string tag && string.Equals(tag, _settings.GallerySortMode, StringComparison.OrdinalIgnoreCase))
            {
                GallerySortComboBox.SelectedItem = cbi;
                break;
            }
        }
    }

    private void GallerySortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settings is null || GalleryPanel is null) return;
        if (GallerySortComboBox.SelectedItem is ComboBoxItem item && item.Tag is string sortTag)
        {
            _settings.GallerySortMode = sortTag;
            _settings.Save();
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        }
    }

    private void ToggleStarClip(string clipKey)
    {
        if (!_settings.StarredClips.Add(clipKey))
            _settings.StarredClips.Remove(clipKey);
        _settings.Save();
    }

    private IEnumerable<FileInfo> ApplyGallerySort(IEnumerable<FileInfo> files)
    {
        return _settings.GallerySortMode switch
        {
            "StarredOnly" => files.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).OrderByDescending(f => f.LastWriteTime),
            "StarredFirst" => files.OrderByDescending(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).ThenByDescending(f => f.LastWriteTime),
            "DateAsc" => files.OrderBy(f => f.LastWriteTime),
            "SizeDesc" => files.OrderByDescending(f => f.Length),
            "SizeAsc" => files.OrderBy(f => f.Length),
            "NameAsc" => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            "NameDesc" => files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderByDescending(f => f.LastWriteTime),
        };
    }

    private IEnumerable<RemoteGalleryFile> ApplyRemoteGallerySort(IEnumerable<RemoteGalleryFile> files)
    {
        return _settings.GallerySortMode switch
        {
            "StarredOnly" => files.Where(f => _settings.StarredClips.Contains(f.Name)).OrderByDescending(f => f.Modified),
            "StarredFirst" => files.OrderByDescending(f => _settings.StarredClips.Contains(f.Name)).ThenByDescending(f => f.Modified),
            "DateAsc" => files.OrderBy(f => f.Modified),
            "SizeDesc" => files.OrderByDescending(f => f.Size),
            "SizeAsc" => files.OrderBy(f => f.Size),
            "NameAsc" => files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase),
            "NameDesc" => files.OrderByDescending(f => f.Name, StringComparer.OrdinalIgnoreCase),
            _ => files.OrderByDescending(f => f.Modified),
        };
    }

    private void UpdateGalleryFooterStats(int count, long totalBytes, string? folderName)
    {
        double mb = totalBytes / (1024.0 * 1024.0);
        string sizeStr = mb >= 1024 ? $"{mb / 1024.0:0.0} GB" : $"{mb:0.#} MB";
        string folderPrefix = folderName is not null ? $"{folderName}: " : "";
        GalleryTotalStatsText.Text = $"{folderPrefix}{count} clip{(count == 1 ? "" : "s")} · {sizeStr}";

        UpdateGalleryStorageBar();
    }

    private void UpdateGalleryStorageBar()
    {
        try
        {
            if (GalleryStorageProgressBar is null || GalleryStorageText is null)
                return;

            if (_galleryIsRemote)
            {
                GalleryStorageProgressBar.Width = 0;
                GalleryStorageText.Text = $"{_settings.PairedPeerName ?? "Remote"} PC";
                GalleryStorageText.Foreground = (Brush)FindResource("Text2");
                return;
            }

            if (_settings.StorageLimitEnabled && _settings.StorageLimitGb > 0)
            {
                double usedGb = GetClipsFolderUsageBytes() / (double)BytesPerGb;
                double limitGb = _settings.StorageLimitGb;
                double ratio = Math.Clamp(usedGb / limitGb, 0.0, 1.0);

                const double maxBarWidth = 110.0;
                GalleryStorageProgressBar.Width = ratio * maxBarWidth;

                bool isOver85 = ratio >= 0.85;
                if (isOver85)
                {
                    GalleryStorageProgressBar.Background = (Brush)FindResource("Rec");
                    GalleryStorageText.Foreground = (Brush)FindResource("Rec");
                }
                else
                {
                    GalleryStorageProgressBar.SetResourceReference(Border.BackgroundProperty, "Accent");
                    GalleryStorageText.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
                }

                GalleryStorageText.Text = $"{usedGb:0.0} out of {limitGb:0.#} GB used";
            }
            else
            {
                string root = Path.GetPathRoot(_settings.ClipsFolder) ?? "C:\\";
                var drive = new DriveInfo(root);
                if (drive.IsReady)
                {
                    long totalBytes = drive.TotalSize;
                    long freeBytes = drive.AvailableFreeSpace;
                    long usedBytes = totalBytes - freeBytes;

                    double usedRatio = Math.Clamp((double)usedBytes / totalBytes, 0.0, 1.0);
                    double freeGb = freeBytes / (double)BytesPerGb;

                    const double maxBarWidth = 110.0;
                    GalleryStorageProgressBar.Width = usedRatio * maxBarWidth;

                    bool isOver85 = usedRatio >= 0.85;
                    if (isOver85)
                    {
                        GalleryStorageProgressBar.Background = (Brush)FindResource("Rec");
                        GalleryStorageText.Foreground = (Brush)FindResource("Rec");
                    }
                    else
                    {
                        GalleryStorageProgressBar.SetResourceReference(Border.BackgroundProperty, "Accent");
                        GalleryStorageText.SetResourceReference(TextBlock.ForegroundProperty, "Text1");
                    }

                    string freeGbText = freeGb >= 100 ? $"{freeGb:0} GBs Free" : $"{freeGb:0.0} GBs Free";
                    GalleryStorageText.Text = freeGbText;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Write($"Gallery storage bar update error: {ex.Message}");
        }
    }


    private void ClearClipsDirectoryButton_Click(object sender, RoutedEventArgs e)
    {
        string clipsFolder = _settings.ClipsFolder;
        if (string.IsNullOrWhiteSpace(clipsFolder) || !Directory.Exists(clipsFolder))
        {
            MessageBox.Show(this, "Your clips folder isn't set or doesn't exist.", "Backtrack");
            return;
        }

        List<string> clipFiles;
        try
        {
            
            
            
            
            
            
            
            clipFiles = Directory.EnumerateFiles(clipsFolder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read the clips folder: {ex.Message}", "Backtrack");
            return;
        }

        if (clipFiles.Count == 0)
        {
            MessageBox.Show(this, "No clips found in your clips folder.", "Backtrack");
            return;
        }

        ShowConfirmDialog(
            $"Permanently delete {clipFiles.Count} clip(s) from \"{clipsFolder}\"? " +
            "Only the clip files will be deleted. Folders, other file types, and subfolders will not be affected.",
            "Delete clips",
            confirmed =>
            {
                if (!confirmed) return;
                int failed = 0;
                foreach (string f in clipFiles)
                {
                    try { File.Delete(f); }
                    catch { failed++; }
                }
                LoadGallery();
                if (failed > 0)
                    MessageBox.Show(this, $"{failed} clip(s) couldn't be deleted (in use, or permissions). The rest were removed.", "Backtrack");
            });
    }


    private static void RevealInExplorer(string filePath) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{filePath}\"") { UseShellExecute = true });


    private async void GalleryTile_Click(object sender, RoutedEventArgs e)
    {
        _currentGalleryFolder = null;
        _currentRemoteGalleryFolder = null;
        
        
        
        
        
        _galleryIsRemote = !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        RefreshGallerySourceTabsVisibility();
        SyncGalleryToolbarUi();

        if (_galleryIsRemote)
        {
            await LoadRemoteGalleryAsync();
            ShowScreen(Screen.Gallery);
        }
        else
        {
            ShowScreen(Screen.Gallery);
            LoadGallery();
        }
    }


        private void RefreshGallerySourceTabsVisibility()
    {
        GallerySourceTabs.Visibility = Visibility.Collapsed;
        bool paired = !string.IsNullOrEmpty(_settings.PairedPeerSecret);
        if (!paired && _galleryIsRemote)
        {
            
            
            
            _galleryIsRemote = false;
            _currentRemoteGalleryFolder = null;
        }
    }


    private void GalleryLocalTab_Click(object sender, RoutedEventArgs e)
    {
        if (!_galleryIsRemote)
            return;
        GalleryFilterBox.Text = string.Empty; 
        _galleryIsRemote = false;
        RefreshGallerySourceTabsVisibility();
        LoadGallery();
    }


    private void GalleryRemoteTab_Click(object sender, RoutedEventArgs e)
    {
        if (_galleryIsRemote || string.IsNullOrEmpty(_settings.PairedPeerSecret))
            return;
        GalleryFilterBox.Text = string.Empty; 
        _galleryIsRemote = true;
        _currentRemoteGalleryFolder = null;
        RefreshGallerySourceTabsVisibility();
        LoadGallery();
    }


        private void RunAutoDeleteOldClips()
    {
        if (!_settings.AutoDeleteOldClipsEnabled)
            return;

        string folder = _settings.ClipsFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        DateTime cutoff = DateTime.Now.AddDays(-_settings.AutoDeleteOldClipsAfterDays);
        List<string> oldClips;
        try
        {
            oldClips = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && File.GetLastWriteTime(f) < cutoff)
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write($"Auto-delete old clips: couldn't scan clips folder: {ex.Message}");
            return;
        }

        if (oldClips.Count == 0)
            return;

        int deleted = 0;
        foreach (string f in oldClips)
        {
            string fileName = Path.GetFileName(f);
            if (_settings.StarredClips.Contains(fileName) || _settings.StarredClips.Contains(f))
                continue;

            if (RecycleBin.Delete(f))
                deleted++;
        }

        AppLog.Write($"Auto-delete old clips: removed {deleted}/{oldClips.Count} clip(s) older than {_settings.AutoDeleteOldClipsAfterDays} day(s).");
        if (deleted > 0)
        {
            _toastOverlay.ShowOldClipsAutoDeleted(deleted, _settings.AutoDeleteOldClipsAfterDays);
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
        }
    }


    private async Task RefreshGalleryCountAsync()
    {
        
        
        
        
        
        if (!string.IsNullOrEmpty(_settings.PairedPeerSecret))
        {
            RemoteGalleryListing? rootListing = await _pairing.ListRemoteGalleryAsync("");
            if (rootListing is not null)
            {
                int total = rootListing.Files.Count;
                foreach (string folder in rootListing.Folders)
                    total += await CountRemoteClipsRecursiveAsync(folder);
                GalleryStatus.Text = total == 1 ? "1 clip" : $"{total} clips";
                return;
            }
            
            
        }

        int count = await Task.Run(CountClips);
        GalleryStatus.Text = count == 1 ? "1 clip" : $"{count} clips";
    }


        private async Task<int> CountRemoteClipsRecursiveAsync(string relativePath)
    {
        RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(relativePath);
        if (listing is null)
            return 0;

        int count = listing.Files.Count;
        foreach (string folder in listing.Folders)
            count += await CountRemoteClipsRecursiveAsync($"{relativePath}/{folder}");
        return count;
    }


    private void GalleryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GalleryFilterPlaceholder.Visibility = GalleryFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _galleryFilterDebounceTimer.Stop();
        _galleryFilterDebounceTimer.Start();
    }


        private void OpenRemoteGalleryFolder(string name)
    {
        GalleryFilterBox.Text = string.Empty; 
        _currentRemoteGalleryFolder = _currentRemoteGalleryFolder is null ? name : $"{_currentRemoteGalleryFolder}/{name}";
        LoadGallery();
    }


    private string RemoteClipRelativePath(string fileName) =>
        _currentRemoteGalleryFolder is null ? fileName : $"{_currentRemoteGalleryFolder}/{fileName}";


        private async Task<List<(string RelativePath, RemoteGalleryFile File)>?> ListAllRemoteClipsAsync()
    {
        var foldersToWalk = new Queue<string?>();
        foldersToWalk.Enqueue(null); 
        var all = new List<(string RelativePath, RemoteGalleryFile File)>();

        while (foldersToWalk.Count > 0)
        {
            string? folder = foldersToWalk.Dequeue();
            RemoteGalleryListing? listing = await _pairing.ListRemoteGalleryAsync(folder ?? "");
            if (listing is null)
                return null;

            foreach (string subfolder in listing.Folders)
                foldersToWalk.Enqueue(folder is null ? subfolder : $"{folder}/{subfolder}");

            foreach (RemoteGalleryFile file in listing.Files)
                all.Add((folder is null ? file.Name : $"{folder}/{file.Name}", file));
        }

        return all;
    }


        private async Task SyncRemoteClipsAsync(IProgress<double>? progress = null)
    {
        List<(string RelativePath, RemoteGalleryFile File)>? all = await ListAllRemoteClipsAsync();
        
        
        if (all is null)
            return;

        var toDownload = new List<(string RelativePath, RemoteGalleryFile File, string DestPath)>();
        foreach ((string relativePath, RemoteGalleryFile file) in all)
        {
            if (_pendingRemoteDeletePaths.Contains(relativePath))
                continue;

            string destPath = GetRemoteClipCachePath(relativePath, file.Name);
            if (File.Exists(destPath))
                continue;

            toDownload.Add((relativePath, file, destPath));
        }

        if (toDownload.Count == 0)
        {
            progress?.Report(1.0);
            return;
        }

        for (int i = 0; i < toDownload.Count; i++)
        {
            (string relativePath, RemoteGalleryFile file, string destPath) = toDownload[i];
            int completed = i; 
            
            
            
            
            
            var fileProgress = progress is null ? null : new Progress<double>(p => progress.Report((completed + p) / toDownload.Count));

            
            
            
            
            await _pairing.DownloadRemoteClipAsync(relativePath, destPath, fileProgress);
            progress?.Report((double)(i + 1) / toDownload.Count);
        }
    }


        private async Task OpenRemoteClipFileLocationAsync(string relativePath, RemoteGalleryFile file)
    {
        string destPath = GetRemoteClipCachePath(relativePath, file.Name);
        if (!File.Exists(destPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            (bool success, string? error) = await _pairing.DownloadRemoteClipAsync(relativePath, destPath);
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't download that clip: {error}", "Backtrack");
                return;
            }
        }
        RevealInExplorerAndClose(destPath);
    }


        private async Task CopyRemoteClipPathAsync(string relativePath, RemoteGalleryFile file)
    {
        string destPath = GetRemoteClipCachePath(relativePath, file.Name);
        if (!File.Exists(destPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            (bool success, string? error) = await _pairing.DownloadRemoteClipAsync(relativePath, destPath);
            if (!success)
            {
                MessageBox.Show(this, $"Couldn't download that clip: {error}", "Backtrack");
                return;
            }
        }
        Clipboard.SetText(destPath);
    }


        private void DeleteRemoteClip(string relativePath, RemoteGalleryFile file)
    {
        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This deletes the real clip on {_settings.PairedPeerName}'s PC (sent to its recycle bin there), not just this view.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                    QueueRemoteDeleteWithUndo(relativePath, file.Name, file);
            });
    }


    private async Task FinishRemoteDeleteAsync(string relativePath, string displayName, RemoteGalleryFile? file)
    {
        (bool success, string? error) = await _pairing.DeleteRemoteClipAsync(relativePath);
        if (!success)
        {
            
            
            
            
            
            _ = Dispatcher.BeginInvoke(() => MessageBox.Show(this, $"Couldn't delete \"{displayName}\": {error}", "Backtrack"));
        }
        else if (file is not null)
        {
            
            
            
            try { File.Delete(GetRemoteClipCachePath(relativePath, file.Name)); } catch {  }
            string? thumbCache = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
            if (thumbCache is not null)
            {
                try { File.Delete(thumbCache); } catch {  }
            }
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (GalleryPanel.Visibility == Visibility.Visible)
                LoadGallery();
            else
                _ = RefreshGalleryCountAsync();
        });
    }


        private string? GetRemoteThumbnailCachePath(string relativePath, DateTime modified, long size)
    {
        if (string.IsNullOrEmpty(_settings.PairedPeerDeviceId))
            return null;

        string cacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Backtrack", "RemoteThumbnails", _settings.PairedPeerDeviceId);
        string key = $"{relativePath}|{modified.Ticks}|{size}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, $"{hash}.jpg");
    }


    private async Task LoadRemoteThumbnailAsync(string relativePath, RemoteGalleryFile file, Image target)
    {
        string? cachePath = GetRemoteThumbnailCachePath(relativePath, file.Modified, file.Size);
        if (cachePath is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        if (!File.Exists(cachePath))
        {
            (bool success, _) = await _pairing.DownloadRemoteThumbnailAsync(relativePath, cachePath);
            if (!success)
                return;
        }

        BitmapImage? bitmap = null;
        if (File.Exists(cachePath))
        {
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(cachePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch
            {
                bitmap = null;
            }
        }

        if (bitmap is not null)
        {
            await target.Dispatcher.InvokeAsync(() => target.Source = bitmap);
        }
    }


    private void ToggleClipSelected(FileInfo file)
    {
        if (!_selectedClipPaths.Add(file.FullName))
            _selectedClipPaths.Remove(file.FullName);
        RefreshGallerySelectionUi();
    }


    private void RefreshGallerySelectionUi()
    {
        int count = _selectedClipPaths.Count;
        GallerySelectionBar.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        GallerySelectionCountText.Text = count == 1 ? "1 selected" : $"{count} selected";

        foreach (var (file, circle, thumb) in _galleryCardSelection)
        {
            bool selected = _selectedClipPaths.Contains(file.FullName);
            circle.Background = selected ? (Brush)FindResource("Green") : new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
            circle.BorderBrush = selected ? (Brush)FindResource("Green") : (Brush)FindResource("Text0");

            
            
            
            
            
            if (count > 0)
                circle.Visibility = Visibility.Visible;
            else if (!thumb.IsMouseOver)
                circle.Visibility = Visibility.Collapsed;
        }
    }


    private void CancelSelection_Click(object sender, RoutedEventArgs e)
    {
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
    }


    private void DeleteSelected_Click(object sender, RoutedEventArgs e)
    {
        List<FileInfo> targets = _galleryCardSelection
            .Where(c => _selectedClipPaths.Contains(c.File.FullName))
            .Select(c => c.File)
            .ToList();
        if (targets.Count == 0)
            return;

        string message = targets.Count == 1
            ? $"Are you sure you want to delete \"{targets[0].Name}\"? This will send it to your recycle bin."
            : $"Are you sure you want to delete {targets.Count} clips? This will send them to your recycle bin.";

        ShowConfirmDialog(message, "Delete", confirmed =>
        {
            if (confirmed)
            {
                _selectedClipPaths.Clear();
                if (targets.Count == 1)
                    QueueDeleteWithUndo(targets[0]);
                else
                    QueueMultiDeleteWithUndo(targets);
            }
        });
    }


    private void MoveSelected_Click(object sender, RoutedEventArgs e)
    {
        List<FileInfo> targets = _galleryCardSelection
            .Where(c => _selectedClipPaths.Contains(c.File.FullName))
            .Select(c => c.File)
            .ToList();
        if (targets.Count == 0)
            return;

        var dialog = new OpenFolderDialog { InitialDirectory = GalleryFolder };
        if (dialog.ShowDialog(this) != true)
            return;

        string destination = dialog.FolderName;
        foreach (FileInfo file in targets)
        {
            try
            {
                string dest = Path.Combine(destination, file.Name);
                if (File.Exists(dest))
                    dest = Path.Combine(destination, $"{Path.GetFileNameWithoutExtension(file.Name)}_{DateTime.Now:HHmmss}{file.Extension}");
                File.Move(file.FullName, dest);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Couldn't move \"{file.Name}\": {ex.Message}", "Backtrack");
            }
        }

        LoadGallery();
    }


    private static string GetThumbnailCachePath(FileInfo file)
    {
        string cacheDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Backtrack", "thumbnails");
        Directory.CreateDirectory(cacheDir);
        
        
        
        string key = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(cacheDir, $"{hash}.jpg");
    }


    
    
    
    
    private static string GetDurationCachePath(FileInfo file) => Path.ChangeExtension(GetThumbnailCachePath(file), ".duration");


    private async Task LoadThumbnailAsync(FileInfo file, Image target, TextBlock? durationTarget = null)
    {
        string? cachePath = await EnsureThumbnailCachedAsync(file);

        long? durationMs = TryGetCachedDurationMs(file);

        if (cachePath is null && durationMs is null)
            return;

        BitmapImage? bitmap = null;
        if (cachePath is not null && File.Exists(cachePath))
        {
            try
            {
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(cachePath, UriKind.Absolute);
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch
            {
                bitmap = null;
            }
        }

        await target.Dispatcher.InvokeAsync(() =>
        {
            if (bitmap is not null)
                target.Source = bitmap;
            if (durationTarget is not null && durationMs is not null)
                durationTarget.Text = FormatDuration(durationMs.Value);
        });
    }


    private void DeleteClip(FileInfo file, Border card)
    {
        ShowConfirmDialog(
            $"Are you sure you want to delete \"{file.Name}\"? This will send it to your recycle bin.",
            "Delete",
            confirmed =>
            {
                if (confirmed)
                {
                    QueueDeleteWithUndo(file);
                }
            });
    }


    private void RefreshPairingStatusUi()
    {
        if (!string.IsNullOrEmpty(_settings.PairedPeerName))
        {
            PairingStatusText.Text = $"Paired with \"{_settings.PairedPeerName}\"";
            UnpairButton.Visibility = Visibility.Visible;
        }
        else
        {
            PairingStatusText.Text = "Not paired";
            UnpairButton.Visibility = Visibility.Collapsed;
        }
        RefreshGallerySourceTabsVisibility();
    }


    private void ChangeClipsFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { InitialDirectory = _settings.ClipsFolder };
            if (dialog.ShowDialog(this) == true)
            {
                _settings.ClipsFolder = dialog.FolderName;
                _settings.Save();
                ClipsFolderText.Text = _settings.ClipsFolder;
                _ = RefreshGalleryCountAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't change the clips folder: {ex.Message}", "Backtrack");
        }
    }

}
