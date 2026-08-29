using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Backtrack.Core;
using Backtrack.Interop;
using Backtrack.Obs;
using Backtrack.Pairing;
using Microsoft.Win32;

namespace Backtrack;

public partial class MainWindow : Window
{
    private string GalleryFolder => _currentGalleryFolder ?? _settings.ClipsFolder;

    private void SyncGalleryToolbarUi()
    {
        if (_settings is null || GallerySortComboBox is null) return;
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
        if (string.IsNullOrWhiteSpace(clipKey)) return;
        string fileName = Path.GetFileName(clipKey);
        bool isStarred = _settings.StarredClips.Contains(clipKey) || _settings.StarredClips.Contains(fileName);
        bool newStarred = !isStarred;

        if (newStarred)
        {
            _settings.StarredClips.Add(clipKey);
            _settings.StarredClips.Add(fileName);
        }
        else
        {
            _settings.StarredClips.Remove(clipKey);
            _settings.StarredClips.Remove(fileName);
        }

        _settings.Save();
        if (_pairing is not null)
        {
            _ = _pairing.SendSyncStarredAsync(clipKey, newStarred);
        }
    }

    private void SaveClipMarkers(string clipKey, List<double> markers, bool syncToRemote = true)
    {
        if (string.IsNullOrWhiteSpace(clipKey)) return;
        string fileName = Path.GetFileName(clipKey);
        markers.Sort();
        _settings.ClipMarkers[clipKey] = markers;
        if (!string.Equals(fileName, clipKey, StringComparison.OrdinalIgnoreCase))
        {
            _settings.ClipMarkers[fileName] = markers;
        }
        _settings.Save();

        if (syncToRemote && _pairing is not null)
        {
            _ = _pairing.SendSyncClipMarkersAsync(clipKey, markers);
        }
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
                if (_lastRemoteStorageInfo is not null)
                {
                    if (_lastRemoteStorageInfo.StorageLimitEnabled && _lastRemoteStorageInfo.StorageLimitGb > 0)
                    {
                        double usedGb = _lastRemoteStorageInfo.ClipsFolderBytes / (double)BytesPerGb;
                        double limitGb = _lastRemoteStorageInfo.StorageLimitGb;
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
                        return;
                    }
                    else if (_lastRemoteStorageInfo.DriveTotalBytes > 0)
                    {
                        long totalBytes = _lastRemoteStorageInfo.DriveTotalBytes;
                        long freeBytes = _lastRemoteStorageInfo.DriveFreeBytes;
                        long usedBytes = Math.Max(0, totalBytes - freeBytes);

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
                        return;
                    }
                }

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


    private void GalleryFilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        GalleryFilterPlaceholder.Visibility = GalleryFilterBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _galleryFilterDebounceTimer?.Stop();
        _galleryFilterDebounceTimer?.Start();
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


    private void LoadGallery()
    {
        if (_galleryIsRemote)
        {
            _ = LoadRemoteGalleryAsync();
            return;
        }

        GalleryGrid.Children.Clear();
        _galleryCardSelection.Clear();
        _selectedClipPaths.Clear();
        RefreshGallerySelectionUi();
        UpdateGalleryPathBar();

        string folder = GalleryFolder;

        if (!Directory.Exists(folder))
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = $"Folder doesn't exist yet: {folder}\n\nSet a folder that actually has your clips in Settings.",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
                TextWrapping = TextWrapping.Wrap,
                Width = BigWidth() - 40,
            });
            return;
        }

        
        
        
        string filter = GalleryFilterBox.Text.Trim();

        List<DirectoryInfo> subfolders;
        List<FileInfo> files;
        try
        {
            subfolders = Directory.GetDirectories(folder)
                .Select(d => new DirectoryInfo(d))
                .Where(d => filter.Length == 0 || d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            files = Directory.EnumerateFiles(folder)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Where(f => filter.Length == 0 || Path.GetFileNameWithoutExtension(f).Contains(filter, StringComparison.OrdinalIgnoreCase))
                .Select(f => new FileInfo(f))
                
                
                
                
                
                
                
                
                
                
                
                
                
                .Where(f => f.Exists && f.Length > 0 && TryGetCachedDurationMs(f) is not < 2000)
                .ToList();

            if (_settings.GalleryStarredOnly)
            {
                files = files.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).ToList();
            }

            files = ApplyGallerySort(files).ToList();
        }
        catch (Exception ex)
        {
            GalleryGrid.Children.Add(new TextBlock { Text = $"Couldn't read that folder: {ex.Message}", Foreground = (Brush)FindResource("Rec"), TextWrapping = TextWrapping.Wrap });
            return;
        }

        if (subfolders.Count == 0 && files.Count == 0)
        {
            GalleryGrid.Children.Add(new TextBlock
            {
                Text = filter.Length == 0 ? "No clips in this folder yet." : $"Nothing here matches \"{filter}\".",
                FontSize = 12,
                Foreground = (Brush)FindResource("Text2"),
            });
            GalleryStatus.Text = "0 clips";
            UpdateGalleryFooterStats(0, 0, _currentGalleryFolder is null ? null : Path.GetFileName(_currentGalleryFolder));
            return;
        }

        string? newestClipPath = GetNewestClipPath();

        foreach (DirectoryInfo dir in subfolders)
        {
            string dirFull = Path.GetFullPath(dir.FullName);
            bool leadsToNewest = newestClipPath is not null
                && newestClipPath.StartsWith(dirFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            GalleryGrid.Children.Add(BuildFolderCard(dir.Name, () => OpenGalleryFolder(dir.FullName), leadsToNewest, dir.FullName));
        }

        foreach (FileInfo file in files)
            GalleryGrid.Children.Add(BuildClipCard(file,
                isNewest: newestClipPath is not null && string.Equals(Path.GetFullPath(file.FullName), newestClipPath, StringComparison.OrdinalIgnoreCase)));

        int totalClipsCount;
        long totalFolderBytes;
        try
        {
            var allFilesInTree = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => VideoExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()) && !_pendingDeletePaths.Contains(Path.GetFullPath(f)))
                .Select(f => new FileInfo(f))
                .Where(f => f.Exists && f.Length > 0)
                .ToList();

            if (_settings.GalleryStarredOnly)
            {
                allFilesInTree = allFilesInTree.Where(f => _settings.StarredClips.Contains(f.Name) || _settings.StarredClips.Contains(f.FullName)).ToList();
            }

            totalClipsCount = allFilesInTree.Count;
            totalFolderBytes = allFilesInTree.Sum(f => f.Length);
        }
        catch
        {
            totalClipsCount = files.Count;
            totalFolderBytes = files.Sum(f => f.Length);
        }

        UpdateGalleryFooterStats(totalClipsCount, totalFolderBytes, _currentGalleryFolder is null ? null : Path.GetFileName(_currentGalleryFolder));

        if (_currentGalleryFolder is null && subfolders.Count > 0)
            _ = RefreshGalleryCountAsync();
        else
            GalleryStatus.Text = files.Count == 1 ? "1 clip" : $"{files.Count} clips";
    }
}
